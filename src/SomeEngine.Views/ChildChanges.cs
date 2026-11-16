using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views;

/// <summary>Child declarations shared by nested builds until their outer build completes.</summary>
internal sealed class ChildChanges
{
    private readonly List<IChildData> _previousData = new();
    private readonly List<Entity> _removedChildren = new();
    private List<ComponentCopy>? _animationWrites;

    internal int DataCount => _previousData.Count;
    internal int RemovedCount => _removedChildren.Count;
    internal int AnimationCount => _animationWrites?.Count ?? 0;

    internal void WriteAnimation(ComponentCopy copy)
    {
        var writes = _animationWrites ??= new List<ComponentCopy>();
        if (!writes.Contains(copy))
        {
            copy.RetainAnimations();
            writes.Add(copy);
        }
    }

    internal void CopyTo(ChildChanges changes, int firstData, int firstRemoval, int firstAnimation)
    {
        for (int index = firstData; index < _previousData.Count; index++)
        {
            IChildData data = _previousData[index];
            data.Retain();
            changes._previousData.Add(data);
        }
        for (int index = firstRemoval; index < _removedChildren.Count; index++)
            changes._removedChildren.Add(_removedChildren[index]);
        if (_animationWrites is { } writes)
            for (int index = firstAnimation; index < writes.Count; index++)
            {
                changes.WriteAnimation(writes[index]);
            }
    }

    internal void Clear()
    {
        _previousData.Clear();
        _removedChildren.Clear();
        _animationWrites?.Clear();
    }

    internal void RememberOrder(Entity parent, Entity[] children)
    {
        ChildOrder? data = null;
        RememberOrder(parent, children, ref data);
    }

    internal void RememberOrder(Entity parent, Entity[] children, ref ChildOrder? data)
    {
        data ??= new();
        data.Capture(parent, children);
        _previousData.Add(data);
    }

    internal void RememberParameters<T>(World world, Entity entity, in T value) where T : struct, IView =>
        _previousData.Add(new ChildParameters<T>(world, entity, value));

    internal void RememberAddedChild(Entity entity) => _previousData.Add(new AddedChild(entity));

    internal void RememberComponent<T>(World world, Entity entity) where T : struct
    {
        ChildComponent<T>? data = null;
        RememberComponent(world, entity, ref data);
    }

    internal void RememberComponent<T>(World world, Entity entity, ref ChildComponent<T>? data) where T : struct
    {
        data ??= new ChildComponent<T>(entity);
        data.Capture(world);
        _previousData.Add(data);
    }

    internal void RememberReads(World world, Entity entity)
    {
        Dependency[]? reads = null;
        if (world.Has<Buffer<Dependency>>(entity))
            {
                using var buffer = world.ReadBuffer<Dependency>(entity);
                reads = buffer.AsSpan().ToArray();
            }
        ObjectDependencyList? objects = world.Has<ObjectDependencies>(entity)
            ? world.Read<ObjectDependencies>(entity).Reads : null;
        _previousData.Add(new ChildReads(entity, reads, objects));
        if (objects is not null)
            world.Remove<ObjectDependencies>(entity);
    }

    internal ComputationList? RememberComputations(World world, Entity entity)
    {
        ChildComputations? data = null;
        return RememberComputations(world, entity, ref data);
    }

    internal ComputationList? RememberComputations(World world, Entity entity, ref ChildComputations? data)
    {
        ComputationList? computations = world.Has<Computations>(entity)
            ? world.Read<Computations>(entity).List : null;
        data ??= new();
        data.Capture(world, entity, computations);
        _previousData.Add(data);
        if (computations is not null)
            world.Remove<Computations>(entity);
        return computations;
    }

    internal void RemoveChild(Entity entity) => _removedChildren.Add(entity);

    internal void Restore(World world, int firstData, int firstRemoval, int firstAnimation)
    {
        for (int index = _previousData.Count - 1; index >= firstData; index--)
            _previousData[index].Restore(world);
        if (_animationWrites is { } writes)
            for (int index = writes.Count - 1; index >= firstAnimation; index--)
                writes[index].RestoreAnimations(world);
        _previousData.RemoveRange(firstData, _previousData.Count - firstData);
        _removedChildren.RemoveRange(firstRemoval, _removedChildren.Count - firstRemoval);
        _animationWrites?.RemoveRange(firstAnimation, AnimationCount - firstAnimation);
    }

    internal void CommitAnimations(World world, int firstAnimation)
    {
        if (_animationWrites is { } writes)
            for (int index = firstAnimation; index < writes.Count; index++)
                writes[index].CommitAnimations(world);
    }

    internal void ReleaseChildren(World world)
    {
        CommitAnimations(world, 0);
        Complete();
        foreach (Entity entity in _removedChildren)
            if (world.IsAlive(entity))
                Hierarchy<ElementTree>.DestroySubtree(world, entity);
    }

    internal void Complete()
    {
        if (_animationWrites is { } writes)
            foreach (ComponentCopy copy in writes)
                copy.CompleteAnimations();
        foreach (IChildData data in _previousData)
            data.Complete();
    }

    internal interface IChildData
    {
        void Retain() { }
        void Restore(World world);
        void Complete() { }
    }

    private static void RestoreValue<T>(World world, Entity entity, in T value) where T : struct
    {
        T restored = value;
        T presented = world.ReadValue<T>(entity);
        AnimatedFields.PreservePresentation(world, entity, ref restored, in presented);
        if (EqualityComparer<T>.Default.Equals(presented, restored))
            world.Overwrite(entity, in restored);
        else
            world.Set(entity, in restored);
    }

    internal sealed class ChildOrder : IChildData
    {
        private Entity _parent;
        private Entity[] _children = [];
        private int _buildCount;

        internal void Capture(Entity parent, Entity[] children)
        {
            _parent = parent;
            _children = children;
            _buildCount = 1;
        }

        public void Retain() => _buildCount++;

        public void Restore(World world)
        {
            Entity parent = _parent;
            Entity[] children = _children;
            var currentChildren = ElementTree.CopyChildren(world, parent);
            for (int index = 0; index < children.Length; index++)
            {
                if (currentChildren[index] == children[index])
                    continue;
                Hierarchy<ElementTree>.SetParent(world, children[index], parent, index);
                currentChildren = ElementTree.CopyChildren(world, parent);
            }
            Complete();
        }

        public void Complete()
        {
            if (--_buildCount == 0)
            {
                _parent = default;
                _children = [];
            }
        }
    }

    private sealed class ChildParameters<T>(World world, Entity entity, T value) : IChildData where T : struct, IView
    {
        private readonly ViewType _viewType = world.Read<ViewType>(entity);
        private readonly bool _buildRequested = world.Has<BuildRequest>(entity);
        private readonly ulong _parameterWriteVersion = T.Mode == BuildMode.Rerun ? world.ComponentWriteVersion<T>(entity) : 0;
        private readonly ulong? _parameterVersion = world.Has<ParameterVersion>(entity) ? world.Read<ParameterVersion>(entity).Value : null;

        public void Restore(World world)
        {
            RestoreValue(world, entity, in value);
            world.Overwrite(entity, in _viewType);
            if (_buildRequested && !world.Has<BuildRequest>(entity))
                world.Set(entity, default(BuildRequest));
            if (T.Mode != BuildMode.Rerun)
                return;
            if (_parameterVersion is ulong previous)
            {
                ulong version = previous == _parameterWriteVersion ? world.ComponentWriteVersion<T>(entity) : previous;
                world.Overwrite(entity, new ParameterVersion(version));
            }
            else if (world.Has<ParameterVersion>(entity))
                world.Remove<ParameterVersion>(entity);
        }
    }

    internal sealed class ChildComponent<T>(Entity entity) : IChildData where T : struct
    {
        private bool _exists;
        private T _value;

        internal void Capture(World world)
        {
            _exists = world.Has<T>(entity);
            _value = _exists ? world.ReadValue<T>(entity) : default;
        }

        internal void Clear() => _value = default;

        public void Restore(World world)
        {
            if (_exists)
                RestoreValue(world, entity, in _value);
            else if (world.Has<T>(entity))
                world.Discard<T>(entity);
        }
    }

    internal sealed class ChildComputations : IChildData
    {
        private World? _world;
        private Entity _entity;
        private ComputationList? _computations;
        private int _buildCount;
        private bool _restored;

        internal void Capture(World world, Entity entity, ComputationList? computations)
        {
            _world = world;
            _entity = entity;
            _computations = computations;
            _buildCount = 1;
            _restored = false;
        }

        public void Retain() => _buildCount++;

        public void Restore(World world)
        {
            Entity entity = _entity;
            ComputationList? computations = _computations;
            if (world.Has<Computations>(entity))
            {
                ComputationList current = world.Read<Computations>(entity).List;
                if (!ReferenceEquals(current, computations))
                    current.ReleaseChildren(retained: computations);
                if (computations is not null)
                    world.Set(entity, new Computations(computations));
                else
                    world.Remove<Computations>(entity);
            }
            else if (computations is not null)
                world.Set(entity, new Computations(computations));
            _restored = true;
            Complete();
        }

        public void Complete()
        {
            if (--_buildCount != 0) return;
            World world = _world!;
            Entity entity = _entity;
            ComputationList? computations = _computations;
            _world = null;
            _entity = default;
            _computations = null;
            if (!_restored)
                computations?.ReleaseChildren(retained: world.IsAlive(entity) && world.Has<Computations>(entity)
                    ? world.Read<Computations>(entity).List : null);
        }
    }

    private sealed class ChildReads(Entity entity, Dependency[]? reads, ObjectDependencyList? objects) : IChildData
    {
        public void Restore(World world)
        {
            if (reads is not null)
                {
                    using var buffer = world.GetBuffer<Dependency>(entity);
                    buffer.Clear();
                    foreach (Dependency value in reads)
                        buffer.Add(value);
                }
            else if (world.Has<Buffer<Dependency>>(entity))
                world.RemoveBuffer<Dependency>(entity);
            if (objects is not null)
            {
                var dependencies = new ObjectDependencies(objects);
                if (world.Has<ObjectDependencies>(entity))
                    world.Set(entity, dependencies);
                else
                    world.Add(entity, dependencies);
            }
            else if (world.Has<ObjectDependencies>(entity))
                world.Remove<ObjectDependencies>(entity);
        }
    }

    private sealed class AddedChild(Entity entity) : IChildData
    {
        public void Restore(World world) => Hierarchy<ElementTree>.DestroySubtree(world, entity);
    }
}
