using SomeEngine.ECS;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Runtime;

/// <summary>Content and game-world callbacks for an application scene.</summary>
public sealed class ApplicationScene(string contentDirectory)
{
    public string ContentDirectory { get; } = Path.GetFullPath(contentDirectory);
    public uint GeometryMemoryBytes { get; init; } = 256u * 1024 * 1024;
    public uint DeformCacheBytes { get; init; } = 64u * 1024 * 1024;
    public float LodThreshold { get; init; } = 1;
    public Action<ApplicationFrame>? Initialize { get; init; }
    public Action<ApplicationFrame>? UpdateFrame { get; init; }
    /// <summary>Receives this frame's Cluster results after GPU completion. Supplying this callback enables synchronous frame diagnostics.</summary>
    public Action<ClusterFrameMetrics>? FrameRendered { get; init; }
    /// <summary>Receives complete CPU/GPU frame timing after GPU completion, preserving asynchronous frame scheduling.</summary>
    public Action<ApplicationFrameTiming>? FrameMeasured { get; init; }
    /// <summary>Describes the application's workload when the scene measurement completes.</summary>
    public Func<object>? DescribeWorkload { get; init; }
}

/// <summary>The game world and ordered systems available before the frame's extraction.</summary>
public sealed class ApplicationFrame
{
    internal ApplicationFrame(World world, GameFrameSystems systems, SceneEdit scene) =>
        (World, Systems, Scene) = (world, systems, scene);

    public World World { get; }
    public GameFrameSystems Systems { get; }
    public SceneEdit Scene { get; }
    public int Index { get; internal set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public double DeltaSeconds { get; internal set; }
    public double SimulationSeconds { get; internal set; }
}
