using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views;

/// <summary>Child declarations shared by nested builds until their outer build completes.</summary>
internal sealed class ChildChanges
{
    private readonly List<IChildData> _previousData = new();
    private readonly List<Entity> _removedChildren = new();

    internal int DataCount => _previousData.Count;
    internal int RemovedCount => _removedChildren.Count;

    internal void Clear()
    {
        _previousData.Clear();
        _removedChildren.Clear();
    }

    internal void RememberOrder(Entity parent, HierarchyChildrenSnapshot<ElementTree> children) =>
        _previousData.Add(new ChildOrder(parent, children));

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
        if (world.HasBuffer<Dependency>(entity))
            world.ExecuteBufferRead<Dependency, Dependency[]?>(entity, ref reads,
                static (buffer, ref values) => values = buffer.AsSpan().ToArray());
        ObjectDependencyList? objects = world.Has<ObjectDependencies>(entity)
            ? world.Read<ObjectDependencies>(entity).Reads : null;
        _previousData.Add(new ChildReads(entity, reads, objects));
        if (objects is not null)
            world.Remove<ObjectDependencies>(entity);
    }

    internal void RememberComputations(World world, Entity entity)
    {
        ComputationList? computations = world.HasSparse<Computations>(entity)
            ? world.ReadSparse<Computations>(entity).List : null;
        _previousData.Add(new ChildComputations(entity, computations));
        if (computations is not null)
            world.RemoveSparse<Computations>(entity);
    }

    internal void RemoveChild(Entity entity) => _removedChildren.Add(entity);

    internal void Restore(World world, int firstData, int firstRemoval)
    {
        for (int index = _previousData.Count - 1; index >= firstData; index--)
            _previousData[index].Restore(world);
        _previousData.RemoveRange(firstData, _previousData.Count - firstData);
        _removedChildren.RemoveRange(firstRemoval, _removedChildren.Count - firstRemoval);
    }

    internal void ReleaseChildren(World world)
    {
        foreach (IChildData data in _previousData)
            data.Complete();
        foreach (Entity entity in _removedChildren)
            if (world.IsAlive(entity))
                Hierarchy<ElementTree>.DestroySubtree(world, entity);
    }

    internal interface IChildData
    {
        void Restore(World world);
        void Complete() { }
    }

    private sealed class ChildOrder(Entity parent, HierarchyChildrenSnapshot<ElementTree> children) : IChildData
    {
        public void Restore(World world)
        {
            var currentChildren = Hierarchy<ElementTree>.GetChildren(world, parent);
            for (int index = 0; index < children.Count; index++)
            {
                if (currentChildren[index] == children[index])
                    continue;
                Hierarchy<ElementTree>.SetParent(world, children[index], parent, index);
                currentChildren = Hierarchy<ElementTree>.GetChildren(world, parent);
            }
        }
    }

    private sealed class ChildParameters<T>(World world, Entity entity, T value) : IChildData where T : struct, IView
    {
        private readonly ViewType _viewType = world.Read<ViewType>(entity);
        private readonly bool _buildRequested = world.Has<BuildRequest>(entity);
        private readonly uint _parameterWriteVersion = T.Mode == BuildMode.Rerun ? world.ComponentDataWriteVersion<T>(entity) : 0;
        private readonly uint? _parameterVersion = world.Has<ParameterVersion>(entity) ? world.Read<ParameterVersion>(entity).Value : null;

        public void Restore(World world)
        {
            world.OverwriteComponentData(entity, in value);
            world.OverwriteComponentData(entity, in _viewType);
            if (_buildRequested && !world.Has<BuildRequest>(entity))
                world.AddTag<BuildRequest>(entity);
            if (T.Mode != BuildMode.Rerun)
                return;
            if (_parameterVersion is uint previous)
            {
                uint version = previous == _parameterWriteVersion ? world.ComponentDataWriteVersion<T>(entity) : previous;
                world.OverwriteComponentData(entity, new ParameterVersion(version));
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
            _exists = world.HasComponentData<T>(entity);
            _value = _exists ? world.ReadComponentData<T>(entity) : default;
        }

        internal void Clear() => _value = default;

        public void Restore(World world)
        {
            if (_exists)
                world.OverwriteComponentData(entity, in _value);
            else if (world.HasComponentData<T>(entity))
                world.DiscardComponentData<T>(entity);
        }
    }

    private sealed class ChildComputations(Entity entity, ComputationList? computations) : IChildData
    {
        public void Restore(World world)
        {
            if (world.HasSparse<Computations>(entity))
            {
                world.ReadSparse<Computations>(entity).List.ReleaseChildren();
                if (computations is not null)
                    world.ReplaceSparse(entity, new Computations(computations));
                else
                    world.RemoveSparse<Computations>(entity);
            }
            else if (computations is not null)
                world.AddSparse(entity, new Computations(computations));
        }

        public void Complete() => computations?.ReleaseChildren();
    }

    private sealed class ChildReads(Entity entity, Dependency[]? reads, ObjectDependencyList? objects) : IChildData
    {
        public void Restore(World world)
        {
            if (reads is not null)
                world.ExecuteBufferWrite<Dependency, Dependency[]>(entity, ref reads, static (buffer, ref values) =>
                {
                    buffer.Clear();
                    foreach (Dependency value in values)
                        buffer.Add(value);
                });
            else if (world.HasBuffer<Dependency>(entity))
                world.RemoveBuffer<Dependency>(entity);
            if (objects is not null)
            {
                var dependencies = new ObjectDependencies(objects);
                if (world.Has<ObjectDependencies>(entity))
                    world.Replace(entity, dependencies);
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
