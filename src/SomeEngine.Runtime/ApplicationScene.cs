using SomeEngine.Graphics;
using SomeEngine.Render.Cluster;

namespace SomeEngine.Runtime;

/// <summary>Application content, rendering settings and callbacks executed on the application thread.</summary>
public sealed class ApplicationScene(string contentDirectory)
{
    private readonly List<IApplicationGeometryFeature> _geometryFeatures = [];
    internal IReadOnlyList<IApplicationGeometryFeature> GeometryFeatures => _geometryFeatures;

    public string ContentDirectory { get; set; } = contentDirectory;
    public RuntimeStartupOptions Options { get; set; } = RuntimeStartupOptions.Parse([]);
    public uint GeometryMemoryBytes { get; set; } = 256u * 1024 * 1024;
    public string? StartupTraceOutput { get; set; }
    public bool DebugUiEnabled { get; set; } = true;
    public Action<long>? SceneStarted { get; set; }
    public Action<ApplicationFrame>? UpdateFrame { get; set; }
    public Action? DrawDebugUi { get; set; }
    public Action<ApplicationFrame>? FrameCompleted { get; set; }
    public Action<ApplicationResult>? SceneCompleted { get; set; }
    public bool ReloadRequested { get; internal set; }

    /// <summary>Installs the feature for each scene run, before extraction and rendering begin.</summary>
    public void InstallGeometry(IApplicationGeometryFeature feature) => _geometryFeatures.Add(feature);

    /// <summary>Loads the current content and settings after this scene run completes.</summary>
    public void Reload() => ReloadRequested = true;
}

/// <summary>Access to the application's current frame; rendering diagnostics are read on request.</summary>
public sealed class ApplicationFrame
{
    private readonly RuntimeInput _input;
    private readonly ClusterRenderResources _rendering;
    private readonly ApplicationStartupTiming _startup;

    internal ApplicationFrame(SceneEdit scene, RuntimeInput input, ClusterRenderResources rendering,
        ApplicationStartupTiming startup, AdapterInfo adapter)
        => (Scene, _input, _rendering, _startup, Adapter) = (scene, input, rendering, startup, adapter);

    public SceneEdit Scene { get; }
    public AdapterInfo Adapter { get; }
    public int Index { get; internal set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public ApplicationFrameTimes Timing { get; internal set; }
    public ApplicationStartupTimes? Startup => _startup.Result;
    public bool WasKeyPressed(int virtualKey) => _input.WasKeyPressed(virtualKey);
    public ClusterRenderDiagnostics GetRenderingDiagnostics() => _rendering.CaptureDiagnostics();
}

public readonly record struct ApplicationFrameTimes(double ElapsedMs, double PrepareMs, double ReconcileMs, double ExecuteMs);
