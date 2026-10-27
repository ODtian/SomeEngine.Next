using SomeEngine.ECS;
using SomeEngine.ECS.Registry;
using SomeEngine.Physics;

namespace DefaultGame;

internal interface ISceneBodyBindings
{
    ReadOnlySpan<int> ComponentIds { get; }
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
        ComponentMetadata<DeviceBodyBinding>.Id,
        ComponentMetadata<DeviceAvbdEquationBinding>.Id,
        ComponentMetadata<DeviceGeometryBinding>.Id,
    };

    public void Write(BundleWriteView view, int index)
    {
        view.Write(new DeviceBodyBinding(bodies, index));
        view.Write(new DeviceAvbdEquationBinding(equations, index));
        view.Write(new DeviceGeometryBinding(geometry, index));
    }
}
