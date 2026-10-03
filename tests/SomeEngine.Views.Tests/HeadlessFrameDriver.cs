using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Views.Tests;

/// <summary>Drives the available interface systems in frame order through their public APIs.</summary>
public sealed class HeadlessFrameDriver : IDisposable
{
    private readonly SystemGroup<ImmediateSystemContext> _systems;

    public HeadlessFrameDriver()
    {
        World = new World();
        _systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
        _systems.Add(new ViewRebuildSystem());
    }

    public World World { get; }

    public void Frame() => _systems.Update();

    public void Dispose()
    {
        _systems.Dispose();
        World.Dispose();
    }
}
