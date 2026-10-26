using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneDistanceRanges : IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _ranges, _states;
    private readonly WriteDeviceConstraintInputs<DistanceRange, DeviceDistanceRangeConstraint, DeviceDistanceRangeSource> _inputs;
    private readonly DeviceAvbdStateInputs<AvbdDistanceRangeState, DeviceDistanceRangeConstraint,
        DeviceAvbdDistanceRangeState, DeviceAvbdDistanceRangeStateSource> _stateInputs;

    internal DeviceSceneDistanceRanges(IGraphicsBackend backend, Device device, World world, ReadOnlySpan<Entity> ranges)
    {
        _world = world;
        int capacity = Math.Max(1, ranges.Length);
        Data = new(backend, device, capacity, "DeviceDistanceRanges");
        States = new(backend, device, capacity, "DeviceAvbdDistanceRangeStates");
        for (int index = 0; index < ranges.Length; index++)
        {
            world.Add(ranges[index], new DeviceConstraintBinding<DeviceDistanceRangeConstraint>(Data, index));
            world.Add(ranges[index], new DeviceAvbdState<DeviceAvbdDistanceRangeState>(States));
        }
        _ranges = world.Query(new QueryDefinitionBuilder().Read<DistanceRange>()
            .Read<UndirectedRelationEndpoints<DistanceRange>>().Read<DeviceConstraintBinding<DeviceDistanceRangeConstraint>>());
        _states = world.Query(new QueryDefinitionBuilder().Read<AvbdDistanceRangeState>()
            .Read<DeviceConstraintBinding<DeviceDistanceRangeConstraint>>().Read<DeviceAvbdState<DeviceAvbdDistanceRangeState>>());
        _inputs = Data.WriteInputs<DistanceRange, DeviceDistanceRangeSource>(world, _ranges, default, 64);
        _stateInputs = new(backend, device, world, _states, Data, States, default);
        Selection = new(backend, device, world, _ranges, Data, "DeviceDistanceRangesSelection");
        StateSelection = new(backend, device, world, _ranges, Data, new(States), "DeviceDistanceRangeAvbdSelection");
    }

    internal DeviceConstraintData<DeviceDistanceRangeConstraint> Data { get; }
    internal DeviceConstraintState<DeviceAvbdDistanceRangeState> States { get; }
    internal DeviceConstraintSelection<DistanceRange, DeviceDistanceRangeConstraint> Selection { get; }
    internal DeviceConstraintSelection<DistanceRange, DeviceDistanceRangeConstraint, DeviceAvbdStateRows<DeviceAvbdDistanceRangeState>> StateSelection { get; }
    internal UploadDeviceSceneDistanceRanges Upload() => new(this);

    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        Data.AddBindings(bindings);
        States.AddBindings(bindings);
    }

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        dependency = Data.BeginInputs().Schedule(in context, dependency);
        dependency = _inputs.Schedule(in context, dependency);
        dependency = Data.UploadInputs().Schedule(in context, dependency);
        return _stateInputs.Upload().Schedule(in context, dependency);
    }

    public void Dispose()
    {
        StateSelection.Dispose(); Selection.Dispose(); _stateInputs.Dispose(); _inputs.Dispose();
        States.Dispose(); Data.Dispose();
        _world.ReleaseQuery(_states); _world.ReleaseQuery(_ranges);
    }
}

internal readonly struct UploadDeviceSceneDistanceRanges(DeviceSceneDistanceRanges ranges) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => ranges.Schedule(in context, dependency);
}
