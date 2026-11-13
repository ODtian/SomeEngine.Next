using SomeEngine.ECS;
using SomeEngine.Graphics;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Runtime;

/// <summary>Application content, rendering settings and callbacks executed on the application thread.</summary>
public sealed class ApplicationScene(string contentDirectory)
{
    private readonly List<IApplicationGeometryFeature> _geometryFeatures = [];
    internal IReadOnlyList<IApplicationGeometryFeature> GeometryFeatures => _geometryFeatures;

    private string? _requestedContentDirectory;
    internal string? ShaderSourceDirectory { get; private set; }

    public string ContentDirectory { get; set; } = contentDirectory;
    public ApplicationOptions Options { get; set; } = ApplicationOptions.Parse([]);
    public uint GeometryMemoryBytes { get; set; } = 256u * 1024 * 1024;
    public float LodThreshold { get; set; } = 0.25f;
    public ulong GeometryBvhMemoryBytes { get; set; } = 64ul * 1024 * 1024;
    public string? StartupTraceOutput { get; set; }
    public bool DebugUiEnabled { get; set; } = true;
    public bool MeasureInterface { get; set; }
    public Action<long>? SceneStarted { get; set; }
    public Action<ApplicationFrame>? Initialize { get; set; }
    public Action<ApplicationFrame>? Shutdown { get; set; }
    public bool InterfaceEnabled { get; set; }
    public Action<ApplicationFrame>? UpdateFrame { get; set; }
    public Action? DrawDebugUi { get; set; }
    public Action<ApplicationFrame>? FrameCompleted { get; set; }
    /// <summary>Receives this frame's Cluster results after GPU completion. Supplying this callback enables synchronous frame diagnostics.</summary>
    public Action<ClusterFrameMetrics>? FrameRendered { get; set; }
    /// <summary>Receives complete CPU/GPU frame timing after GPU completion, preserving asynchronous frame scheduling.</summary>
    public Action<ApplicationFrameTiming>? FrameMeasured { get; set; }
    public Func<ApplicationRendering, IEnumerable<IApplicationFrameFeature>>? CreateFrameFeatures { get; set; }
    public Func<ApplicationRendering, IApplicationColorFeature>? CreateColorFeature { get; set; }
    public Action<Exception>? ShaderBuildFailed { get; set; }
    public Action<ApplicationResult>? SceneCompleted { get; set; }
    public bool ReloadRequested { get; internal set; }

    /// <summary>Installs the feature for each scene run, before extraction and rendering begin.</summary>
    public void InstallGeometry(IApplicationGeometryFeature feature) => _geometryFeatures.Add(feature);

    [System.Diagnostics.Conditional("SOMEENGINE_DEVELOPMENT")]
    public void WatchShaderSources(string directory) => ShaderSourceDirectory = Path.GetFullPath(directory);

    /// <summary>Loads new game content while the current scene continues rendering.</summary>
    public void LoadScene(string contentDirectory)
    {
        ContentDirectory = Path.GetFullPath(contentDirectory);
        Interlocked.Exchange(ref _requestedContentDirectory, ContentDirectory);
    }

    internal string? TakeSceneRequest() => Interlocked.Exchange(ref _requestedContentDirectory, null);

    /// <summary>Loads the current content and settings after this scene run completes.</summary>
    public void Reload() => ReloadRequested = true;
}

/// <summary>Access to the application's current frame; rendering diagnostics are read on request.</summary>
public sealed class ApplicationFrame
{
    private readonly ApplicationInput _input;
    private readonly ClusterRenderResources _rendering;
    private readonly ApplicationStartupTiming _startup;

    internal ApplicationFrame(World world, GameFrameSystems systems, RenderExtractionSystems extraction, SceneEdit scene, ApplicationInput input, ClusterRenderResources rendering,
        ApplicationStartupTiming startup, AdapterInfo adapter)
        => (World, Systems, Extraction, Scene, _input, _rendering, _startup, Adapter) = (world, systems, extraction, scene, input, rendering, startup, adapter);

    public World World { get; }
    public GameFrameSystems Systems { get; }
    /// <summary>Registers source extractors during Initialize, before the first extraction.</summary>
    public RenderExtractionSystems Extraction { get; }
    public double DeltaSeconds { get; internal set; }
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

public readonly record struct ApplicationFrameTimes(double ElapsedMs, double PrepareMs, double ReconcileMs, double ExecuteMs,
    ViewTiming Views = default, double ExtractionMs = 0, long ExtractionAllocatedBytes = 0, int SimulationFrame = 0);
