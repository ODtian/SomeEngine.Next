using SomeEngine.ECS;
using SomeEngine.Physics;

namespace DefaultGame;

internal interface ISceneBodyBindings
{
    ReadOnlySpan<int> ComponentIds { get; }
    int GetGroup(int index) => 0;
    ReadOnlySpan<int> GetComponentIds(int group) => ComponentIds;
    void Write(BundleWriteView view, int index);
}

internal readonly struct SceneBodyBindings : ISceneBodyBindings
{
    public ReadOnlySpan<int> ComponentIds => ReadOnlySpan<int>.Empty;
    public void Write(BundleWriteView view, int index) { }
}

internal readonly struct SceneDeviceBodyBindings(DeviceBodies bodies, DeviceAvbdBodyEquations equations,
    DeviceCollisionGeometry geometry) : ISceneBodyBindings
{
    public ReadOnlySpan<int> ComponentIds => new int[]
    {
        ComponentId<DeviceBodyBinding>.Value,
        ComponentId<DeviceAvbdEquationBinding>.Value,
        ComponentId<DeviceGeometryBinding>.Value,
    };

    public void Write(BundleWriteView view, int index)
    {
        view.Write(new DeviceBodyBinding(bodies, index));
        view.Write(new DeviceAvbdEquationBinding(equations, index));
        view.Write(new DeviceGeometryBinding(geometry, index));
    }
}

internal readonly struct SceneCpuBodyBindings(PhysicsSceneData data, bool supports, bool reports) : ISceneBodyBindings
{
    private static readonly int[][] s_components =
    [
        [ComponentId<AvbdBodyEquation>.Value],
        [ComponentId<AvbdBodyEquation>.Value, ComponentId<LongRangeBodySupport>.Value,
            ComponentId<Buffer<LongRangeSupportLoad>>.Value, ComponentId<Buffer<LongRangeSupportEquation>>.Value],
        [ComponentId<AvbdBodyEquation>.Value, ComponentId<ReportContacts>.Value],
        [ComponentId<AvbdBodyEquation>.Value, ComponentId<LongRangeBodySupport>.Value,
            ComponentId<Buffer<LongRangeSupportLoad>>.Value, ComponentId<Buffer<LongRangeSupportEquation>>.Value, ComponentId<ReportContacts>.Value],
    ];
    public ReadOnlySpan<int> ComponentIds => s_components[3];
    public int GetGroup(int index) => (supports && data.Bodies[index].Mass > 0 ? 1 : 0) | (reports ? 2 : 0);
    public ReadOnlySpan<int> GetComponentIds(int group) => s_components[group];
    public void Write(BundleWriteView view, int index)
    {
        view.Write(new AvbdBodyEquation());
        if (supports && data.Bodies[index].Mass > 0)
        {
            view.Write(new LongRangeBodySupport());
            view.WriteBuffer<LongRangeSupportLoad>(ReadOnlyMemory<LongRangeSupportLoad>.Empty);
            view.WriteBuffer<LongRangeSupportEquation>(ReadOnlyMemory<LongRangeSupportEquation>.Empty);
        }
        if (reports) view.Write(new ReportContacts());
    }
}
