using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.Graphics;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Runtime;

/// <summary>Application content, rendering settings and callbacks for each scene run.</summary>
public sealed class ApplicationScene(string contentDirectory)
{
    private readonly List<IApplicationGeometryFeature> _geometryFeatures = [];
    private readonly List<ApplicationView> _views = [];
    private int _nextViewImageId = 2;
    internal IReadOnlyList<IApplicationGeometryFeature> GeometryFeatures => _geometryFeatures;

    public string ContentDirectory { get; set; } = contentDirectory;
    /// <summary>Optional ordered shared packs; relative paths use ContentDirectory and the first matching asset wins.</summary>
    public IReadOnlyList<string> AssetPacks { get; set; } = [];
    /// <summary>Selects application settings when the content contains more than one application.</summary>
    public AssetId<ApplicationSettingsAsset>? ApplicationAsset { get; set; }
    public RuntimeStartupOptions Options { get; set; } = RuntimeStartupOptions.Parse([]);
    public uint GeometryMemoryBytes { get; set; } = 256u * 1024 * 1024;
    public float LodThreshold { get; set; } = 0.25f;
    public ulong GeometryBvhMemoryBytes { get; set; } = 64ul * 1024 * 1024;
    public string? StartupTraceOutput { get; set; }
    public bool DebugUiEnabled { get; set; } = true;
    public bool MeasureInterface { get; set; }
    /// <summary>Enables explicit caster-invalidation GPU timestamp requests for this application.</summary>
    public bool MeasureShadowInvalidation { get; init; }
    public Action<long>? SceneStarted { get; set; }
    /// <summary>Adds application component codecs to a fresh registry before each scene load.</summary>
    public Action<SerializationRegistry>? RegisterSceneComponents { get; set; }
    /// <summary>Processes each supported mesh after geometry bindings are attached and before MeshSource is removed.</summary>
    /// <remarks>Runs serially on the loading task, in the original source query order.</remarks>
    public Action<World, Entity>? InitializeMesh { get; set; }
    /// <summary>Creates resources before geometry installation and Initialize, for one scene run.</summary>
    /// <remarks>The single factory returns combined resources, released after renderer cleanup and before World disposal,
    /// including failure unwinding. The factory must clean up partial construction if it throws.</remarks>
    public Func<World, SceneEdit, IDisposable?>? CreateSceneResources { get; set; }
    public Action<ApplicationFrame>? Initialize { get; set; }
    public Action<ApplicationFrame>? Shutdown { get; set; }
    public bool InterfaceEnabled { get; set; }
    /// <summary>Reads platform input and edits this game frame on the window thread, before simulation.</summary>
    public Action<ApplicationFrame>? UpdateFrame { get; set; }
    /// <summary>Edits scene data before camera input, using absolute scene time in seconds.</summary>
    /// <remarks>Frames with UpdateFrame or FrameCompleted, synchronous frames and scene scripts invoke this callback on
    /// the window thread after UpdateFrame. Other frames begin with a window-thread update at time zero and use next-frame
    /// game tasks for the following updates.
    /// Capture, cancellation, camera-history and shadow-refresh requests remain UpdateFrame actions.</remarks>
    public Action<SceneEdit, float>? BeforeCameraInput { get; set; }
    /// <summary>Edits scene data before geometry game systems, using absolute scene time in seconds.</summary>
    /// <remarks>Uses the same thread as BeforeCameraInput: window-thread updates follow camera input; next-frame game
    /// tasks follow BeforeCameraInput. Simulation begins with the first post-bootstrap update.
    /// Capture, cancellation, camera-history and shadow-refresh requests remain UpdateFrame actions.</remarks>
    public Action<SceneEdit, float>? SimulateScene { get; set; }
    public Action? DrawDebugUi { get; set; }
    /// <summary>Draws application ImGui overlays after the optional debug panel.</summary>
    public Action? DrawOverlay { get; set; }
    /// <summary>Additional diagnostic outputs using the main scene camera and prepared rendering data.</summary>
    public IReadOnlyList<ApplicationView> Views => _views;
    public bool ViewsEnabled { get; set; } = true;
    /// <summary>Optional destination rectangle for the main scene image; UI keeps the full window.</summary>
    public ScissorRect? PresentationBounds { get; set; }

    /// <summary>Registers a view before drawing its ImageId in the current ImGui frame.</summary>
    public ApplicationView AddView(ApplicationViewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var view = new ApplicationView(checked(_nextViewImageId++), settings);
        _views.Add(view);
        return view;
    }

    /// <summary>Unregisters a view. Do not draw its ImageId again after removing it.</summary>
    public bool RemoveView(ApplicationView view) => _views.Remove(view);

    public void ClearViews() => _views.Clear();
    /// <summary>Observes the presented frame on the window thread after its game systems and extraction finish.</summary>
    public Action<ApplicationFrame>? FrameCompleted { get; set; }
    /// <summary>Receives this frame's Cluster results after GPU completion. Supplying this callback enables synchronous frame diagnostics.</summary>
    public Action<ClusterFrameMetrics>? FrameRendered { get; set; }
    /// <summary>Receives complete CPU/GPU frame timing after GPU completion, preserving asynchronous frame scheduling.</summary>
    public Action<ApplicationFrameTiming>? FrameMeasured { get; set; }
    public Action<ClusterBlendMetrics>? BlendFrameCompleted { get; set; }
    /// <summary>Creates the color stage for each main-view output size. Its previous instance is disposed after consumers reconnect.</summary>
    public Func<ApplicationRendering, IApplicationColorFeature>? CreateColorFeature { get; set; }
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
    private readonly bool _measureShadowInvalidation;

    internal ApplicationFrame(World world, GameFrameSystems systems, RenderExtractionSystems extraction, SceneEdit scene, RuntimeInput input, ClusterRenderResources rendering,
        ApplicationStartupTiming startup, AdapterInfo adapter, bool measureShadowInvalidation = false)
        => (World, Systems, Extraction, Scene, _input, _rendering, _startup, Adapter, _measureShadowInvalidation)
            = (world, systems, extraction, scene, input, rendering, startup, adapter, measureShadowInvalidation);

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
    internal bool ShadowInvalidationTimingRequested { get; set; }
    /// <summary>Requests diagnostics for this frame if it is submitted. Call during UpdateFrame.</summary>
    public void RequestShadowInvalidationTiming()
    {
        if (!_measureShadowInvalidation) throw new InvalidOperationException("Shadow invalidation timing was not enabled for this application.");
        ShadowInvalidationTimingRequested = true;
    }
    public ApplicationStartupTimes? Startup => _startup.Result;
    public bool WasKeyPressed(int virtualKey) => _input.WasKeyPressed(virtualKey);
    public ClusterRenderDiagnostics GetRenderingDiagnostics() => _rendering.CaptureDiagnostics();
}

public readonly record struct ApplicationFrameTimes(double ElapsedMs, double PrepareMs, double ReconcileMs, double ExecuteMs,
    ViewTiming Views = default, double ExtractionMs = 0, long ExtractionAllocatedBytes = 0, int SimulationFrame = 0);
