using System.Runtime.InteropServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Owners;

internal sealed partial class RelationGraph
{
    private interface IRelationEndpointTracker
    {
        Type PayloadType { get; }

        int PayloadComponentId { get; }

        int EndpointComponentId { get; }

        bool HasPendingPreimages { get; }

        IRelationEndpointTracker CloneDetached(
            RelationGraph owner,
            IRelationTypeState state);

        void Capture(World world, Entity edge);

        void Validate(World world);

        void ValidateDirty(World world);

        void Forget(Entity edge);

        void Commit();

        void Rollback(World world);
    }

    private sealed class RelationEndpointTracker<T> : IRelationEndpointTracker
        where T : struct, IComponent
    {
        private readonly RelationGraph _owner;

        private readonly RelationTypeState<T> _state;
        private readonly int _payloadComponentId;
        private readonly int _endpointComponentId;
        private Dictionary<Entity, EndpointPreimage>? _preimages;

        internal RelationEndpointTracker(
            RelationGraph owner,
            RelationTypeState<T> state,
            int payloadComponentId,
            int endpointComponentId)
        {
            _owner = owner;
            _state = state;
            _payloadComponentId = payloadComponentId;
            _endpointComponentId = endpointComponentId;
        }

        public Type PayloadType => typeof(T);

        public int PayloadComponentId => _payloadComponentId;

        public int EndpointComponentId => _endpointComponentId;

        public bool HasPendingPreimages => _preimages is { Count: > 0 };

        public IRelationEndpointTracker CloneDetached(
            RelationGraph owner,
            IRelationTypeState state)
        {
            var clone = new RelationEndpointTracker<T>(
                owner,
                (RelationTypeState<T>)state,
                _payloadComponentId,
                _endpointComponentId);
            if (_preimages is { Count: > 0 } preimages)
                clone._preimages = new Dictionary<Entity, EndpointPreimage>(preimages);
            return clone;
        }

        public void Capture(World world, Entity edgeEntity)
        {
            Dictionary<Entity, EndpointPreimage>? preimages = _preimages;
            if (preimages?.ContainsKey(edgeEntity) == true)
                return;
            if (!_state.IsEdge(edgeEntity))
            {
                throw new InvalidOperationException(
                    $"Entity {edgeEntity} has protected {typeof(T).Name} endpoints but is not a registered relation edge.");
            }

            var pair = RelationEndpointAccess.ReadCurrent<T>(world, edgeEntity, _state.Schema);
            var edge = new RelationEdge<T>(edgeEntity);
            bool wasDirty = _state.IsDirty(edgeEntity);
            bool hadPendingPlacement = _state.TryGetPendingPlacement(
                edge,
                out var pendingPlacement);
            preimages ??= _preimages = new Dictionary<Entity, EndpointPreimage>();
            preimages.Add(
                edgeEntity,
                new EndpointPreimage(
                    pair,
                    wasDirty,
                    hadPendingPlacement,
                    pendingPlacement));
        }

        public void Validate(World world)
        {
            Dictionary<Entity, EndpointPreimage>? preimages = _preimages;
            if (preimages is not { Count: > 0 })
                return;

            bool hasLiveEdge = false;
            foreach (var (edgeEntity, preimage) in preimages)
            {
                if (!world.IsAlive(edgeEntity) || !_state.IsEdge(edgeEntity))
                    continue;
                hasLiveEdge = true;
                var current = RelationEndpointAccess.ReadCurrent<T>(world, edgeEntity, _state.Schema);
                if (current != preimage.Pair)
                {
                    var edge = new RelationEdge<T>(edgeEntity);
                    var applied = RelationEndpointAccess.ReadAppliedImage<T>(world, edgeEntity);
                    var pending = _state.PendingPlacement(edge);
                    _state.MarkDirty(
                        edge,
                        applied,
                        current,
                        pending.FirstInsertIndex,
                        pending.SecondInsertIndex);
                }
            }

            if (hasLiveEdge)
                ValidateDeferredState(world, _state);
        }

        public void Commit()
        {
            _preimages = null;
        }

        public void ValidateDirty(World world)
        {
            ValidateCommandBatchFinalImage(world, _state);
        }

        public void Forget(Entity edge)
        {
            Dictionary<Entity, EndpointPreimage>? preimages = _preimages;
            if (preimages is null)
                return;
            preimages.Remove(edge);
            if (preimages.Count == 0)
                _preimages = null;
        }

        public void Rollback(World world)
        {
            Dictionary<Entity, EndpointPreimage>? preimages = _preimages;
            if (preimages is not { Count: > 0 })
                return;

            var entries = preimages.ToArray();
            Array.Sort(
                entries,
                static (left, right) => CompareEntities(left.Key, right.Key));
            var trackedEdges = new List<RelationEdge<T>>(entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                Entity edgeEntity = entries[i].Key;
                trackedEdges.Add(new RelationEdge<T>(edgeEntity));
                if (!world.IsAlive(edgeEntity) || !_state.IsEdge(edgeEntity))
                    continue;

                WriteCanonicalEndpoints<T>(
                    world,
                    edgeEntity,
                    _state.Schema,
                    entries[i].Value.Pair);
            }

            _state.ClearDirty(CollectionsMarshal.AsSpan(trackedEdges));
            for (int i = 0; i < entries.Length; i++)
            {
                if (!entries[i].Value.WasDirty ||
                    !world.IsAlive(entries[i].Key) ||
                    !_state.IsEdge(entries[i].Key))
                {
                    continue;
                }

                _state.RestoreDirty(
                    new RelationEdge<T>(entries[i].Key),
                    entries[i].Value.Pair,
                    entries[i].Value.HadPendingPlacement,
                    entries[i].Value.PendingPlacement);
            }
            _preimages = null;
        }

        private readonly record struct EndpointPreimage(
            RelationEndpointPair Pair,
            bool WasDirty,
            bool HadPendingPlacement,
            RelationPendingPlacement PendingPlacement);
    }
}
