using System.Collections.Generic;
using Robust.Shared.Containers;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Robust.Shared.GameObjects
{
    /// <summary>
    ///     Handles moving entities between grids as they move around.
    /// </summary>
    internal sealed class SharedGridTraversalSystem : EntitySystem
    {
        [Dependency] private readonly IMapManagerInternal _mapManager = default!;

        private Stack<MoveEvent> _queuedEvents = new();
        private HashSet<EntityUid> _handledThisTick = new();

        private List<MapGrid> _gridBuffer = new();

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent((ref MoveEvent ev) => _queuedEvents.Push(ev));
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            UpdatesOutsidePrediction = true;

            // Need to queue because otherwise calling HandleMove during FrameUpdate will lead to prediction issues.
            // TODO: Need to check if that's even still relevant since transform lerping fix?
            ProcessChanges();
        }

        private void ProcessChanges()
        {
            var maps = GetEntityQuery<MapComponent>();
            var grids = GetEntityQuery<MapGridComponent>();
            var bodies = GetEntityQuery<PhysicsComponent>();
            var xforms = GetEntityQuery<TransformComponent>();
            var metas = GetEntityQuery<MetaDataComponent>();

            while (_queuedEvents.TryPop(out var moveEvent))
            {
                if (!_handledThisTick.Add(moveEvent.Sender)) continue;

                HandleMove(ref moveEvent, xforms, bodies, maps, grids, metas);
            }

            _handledThisTick.Clear();
        }

        private void HandleMove(
            ref MoveEvent moveEvent,
            EntityQuery<TransformComponent> xforms,
            EntityQuery<PhysicsComponent> bodies,
            EntityQuery<MapComponent> maps,
            EntityQuery<MapGridComponent> grids,
            EntityQuery<MetaDataComponent> metas)
        {
            var entity = moveEvent.Sender;

            if (!metas.TryGetComponent(entity, out var meta) ||
                meta.EntityDeleted ||
                (meta.Flags & MetaDataFlags.InContainer) == MetaDataFlags.InContainer ||
                maps.HasComponent(entity) ||
                grids.HasComponent(entity) ||
                !xforms.TryGetComponent(entity, out var xform) ||
                // If the entity is anchored then we know for sure it's on the grid and not traversing
                xform.Anchored)
            {
                return;
            }

            // DebugTools.Assert(!float.IsNaN(moveEvent.NewPosition.X) && !float.IsNaN(moveEvent.NewPosition.Y));

            // We only do grid-traversal parent changes if the entity is currently parented to a map or a grid.
            var parentIsMap = xform.GridID == GridId.Invalid && maps.HasComponent(xform.ParentUid);
            if (!parentIsMap && !grids.HasComponent(xform.ParentUid))
                return;
            var mapPos = moveEvent.NewPosition.ToMapPos(EntityManager);
            _gridBuffer.Clear();

            // Change parent if necessary
            if (_mapManager.TryFindGridAt(xform.MapID, mapPos, _gridBuffer, xforms, bodies, out var grid))
            {
                // Some minor duplication here with AttachParent but only happens when going on/off grid so not a big deal ATM.
                if (grid.Index != xform.GridID)
                {
                    xform.AttachParent(grid.GridEntityId);
                    var ev = new ChangedGridEvent(entity, xform.GridID, grid.Index);
                    RaiseLocalEvent(entity, ref ev);
                }
            }
            else
            {
                var oldGridId = xform.GridID;

                // Attach them to map / they are on an invalid grid
                if (oldGridId != GridId.Invalid)
                {
                    xform.AttachParent(_mapManager.GetMapEntityIdOrThrow(xform.MapID));
                    var ev = new ChangedGridEvent(entity, oldGridId, GridId.Invalid);
                    RaiseLocalEvent(entity, ref ev);
                }
            }
        }
    }

    [ByRefEvent]
    public readonly struct ChangedGridEvent
    {
        public readonly EntityUid Entity;
        public readonly GridId OldGrid;
        public readonly GridId NewGrid;

        public ChangedGridEvent(EntityUid entity, GridId oldGrid, GridId newGrid)
        {
            Entity = entity;
            OldGrid = oldGrid;
            NewGrid = newGrid;
        }
    }
}
