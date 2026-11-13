using SomeEngine.Transparency.Measurements;
using ImGuiNET;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SomeEngine.Diagnostics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.RenderGraph;
using SomeEngine.Runtime;

namespace DefaultGame;

public sealed record TestScene(string Name, string ContentDirectory, bool Animate = false)
{
    public SimulationFrameWindow? MeasurementFrames { get; init; }
}

/// <summary>The simulation frames selected before comparing scene runs.</summary>
public sealed record SimulationFrameWindow
{
    public SimulationFrameWindow(int firstFrame, int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        _ = checked(firstFrame + frameCount - 1);
        FirstFrame = firstFrame;
        FrameCount = frameCount;
    }

    public int FirstFrame { get; }
    public int FrameCount { get; }

    public (bool Collect, bool Finished, string? Error) SelectFrame(
        int simulationFrame, int recordedFrames, bool warmupComplete, bool ready)
    {
        int lastFrame = FirstFrame + FrameCount - 1;
        if (simulationFrame < FirstFrame) return (false, false, null);
        if (simulationFrame > lastFrame)
            return (false, true, recordedFrames == FrameCount ? null :
                "The requested simulation-frame window was not fully rendered.");
        string? error = simulationFrame != FirstFrame + recordedFrames
            ? "The requested simulation-frame window contains a missing or repeated frame."
            : !warmupComplete ? "Warmup did not complete before the requested simulation-frame window."
            : !ready ? "Scene streaming was not ready within the requested simulation-frame window."
            : null;
        return (true, simulationFrame == lastFrame, error);
    }
}

/// <summary>Shared scene selection and render settings for the interactive test application.</summary>
public sealed partial class SceneTestPlatform
{
    public static TestScene[] ReadScenes(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), SceneJsonContext.Default.TestSceneArray)!;

    public SceneTestPlatform(IReadOnlyList<TestScene> scenes, ApplicationOptions? options = null)
    {
        if (scenes.Count == 0) throw new ArgumentException("At least one test scene is required.", nameof(scenes));
        Scenes = scenes.ToArray();
        Application = new ApplicationScene(scenes[0].ContentDirectory)
        {
            Options = (options ?? ApplicationOptions.Parse([])) with { WindowVSync = false, DynamicScene = scenes[0].Animate, SurfacePanel = true, WorldNameplates = true },
            InterfaceEnabled = true,
            Initialize = frame => InitializeScene?.Invoke(frame.Scene, Current),
            SceneStarted = BeginScene,
            UpdateFrame = UpdateFrame,
            DrawDebugUi = Draw,
            FrameCompleted = FrameCompleted,
            CreateFrameFeatures = CreateMeasurementFeatures,
            SceneCompleted = result => EndScene(result.Error?.Message),
        };
        MeasureBlending = Options.MeasureBlending;
    }

    public IReadOnlyList<TestScene> Scenes { get; }
    public int SelectedScene { get; private set; }
    public ApplicationScene Application { get; }
    public Action<SceneEdit, TestScene>? InitializeScene { get; init; }
    public Action<SceneEdit, TestScene, int>? UpdateScene { get; init; }
    private Func<ApplicationRendering, TestScene, IApplicationColorFeature>? _createColorFeature;
    public Func<ApplicationRendering, TestScene, IApplicationColorFeature>? CreateColorFeature
    {
        get => _createColorFeature;
        init
        {
            _createColorFeature = value;
            Application.CreateColorFeature = value is null ? null : rendering => value(rendering, Current);
        }
    }
    public IReadOnlyDictionary<string, string>? ColorFeatureSettings { get; init; }
    public bool MeasureBlending
    {
        get => Options.MeasureBlending;
        init => Options = Options with { MeasureBlending = value };
    }
    public bool HudOverlayEnabled => Options.HudOverlay;
    public bool HudNumbersChange => Options.HudNumbersChange;
    private ApplicationOptions Options { get => Application.Options; set => Application.Options = value; }
    private bool ReloadRequested => Application.ReloadRequested;
    internal TestScene Current => Scenes[SelectedScene];
    private readonly List<SceneTiming> _results = [];
    private readonly List<ApplicationFrameTimes> _frames = [];
    private readonly Dictionary<int, BlendFrameMetrics> _blendFrames = [];
    private readonly List<(ulong Frame, float LodBias)> _shadowResolutionFeedback = [];
    private bool _shadowDynamicResolutionEnabled;
    private readonly List<SceneFrameWindow> _frameWindows = [];
    private int _firstSampleRenderedFrame;
    private bool _fixedWarmupComplete;
    private string? _fixedWindowError;
    private string? _measurementError;
    private bool _sceneSampleFinished;
    private const string FrameTimingScopes =
        "CPU wall-clock milliseconds. ElapsedMs covers the presented application frame. " +
        "PrepareMs, ReconcileMs and ExecuteMs cover their CPU stages within that frame; " +
        "ExecuteMs includes a requested GPU/output-readback wait. Views and ExtractionMs describe the matching game update. " +
        "SimulationFrame identifies the game state used for the submitted frame and its GPU blend samples.";
    private long _testStarted;
    private long _sceneStarted;
    private long _sampleStarted;
    private long _renderingStarted;
    private int _renderedFrames;
    private int _streamingFrames;
    private double _streamingMs;
    private double _streamingMaxFrameMs;
    private bool _wasStreaming;
    private double _firstFrameMs;
    private double _sceneTimeLimit;
    private int _readyFrames;
    private TraceCapture? _trace;
    private bool _recordTrace;
    private bool _measureAfterStartup;
    private string? _resultDirectory;
    private ApplicationOptions? _interactiveOptions;
    private int _interactiveScene;
    private int _renderWidth;
    private int _renderHeight;
    private string? _graphicsDevice;
    private string? _graphicsDriver;
    public bool IsMeasuring { get; private set; }
    public string? LastReport { get; private set; }
    public string? StartupTraceOutput { get => Application.StartupTraceOutput; init => Application.StartupTraceOutput = value; }
    public ApplicationStartupTimes? Startup { get; private set; }
    public int GeometryMemoryMegabytes
    {
        get => (int)(Application.GeometryMemoryBytes / (1024 * 1024));
        private set => Application.GeometryMemoryBytes = checked((uint)value * 1024 * 1024);
    }
    internal bool ImageRequested { get; private set; }
    internal string? LastImage { get; private set; }

    private void UpdateFrame(ApplicationFrame frame)
    {
        UpdateScene?.Invoke(frame.Scene, Current, frame.Index);
        SetRenderingInfo(frame.Width, frame.Height, frame.Adapter.Name, frame.Adapter.DriverVersion);
        if (!IsMeasuring)
        {
            for (int index = 0; index < Math.Min(Scenes.Count, 3); index++)
                if (frame.WasKeyPressed(0x71 + index)) SelectScene(index);
            if (frame.WasKeyPressed(0x77)) CaptureImage();
            if (frame.WasKeyPressed(0x75)) SetHudOverlay(!HudOverlayEnabled, HudNumbersChange);
            if (frame.WasKeyPressed(0x78)) StartMeasurements();
            if (frame.WasKeyPressed(0x79)) StartMeasurements(recordTrace: true);
        }
        if (ImageRequested)
            frame.Scene.CaptureImage(image =>
            {
                string path = ImageDestination();
                image.SaveBmp(path);
                ImageSaved(path);
            });
    }

    private void FrameCompleted(ApplicationFrame frame)
    {
        if (Startup is null && frame.Startup is { } times) RecordStartup(times);
        if (IsMeasuring && !ReloadRequested) RecordFrame(frame.Timing, frame.GetRenderingDiagnostics());
    }

    private void SetRenderingInfo(int width, int height, string device, string driver)
        => (_renderWidth, _renderHeight, _graphicsDevice, _graphicsDriver) = (width, height, device, driver);

    public void CaptureImage() => ImageRequested = true;

    public void SetHudOverlay(bool enabled, bool changeNumbers = true)
    {
        DevelopmentCheck.Require(!IsMeasuring, "Change the HUD between scene measurements.");
        if (Options.HudOverlay == enabled && Options.HudNumbersChange == changeNumbers) return;
        Options = Options with { HudOverlay = enabled, HudNumbersChange = changeNumbers };
        Application.LoadScene(Current.ContentDirectory);
    }

    private string ImageDestination()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Captures");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"scene-{SelectedScene}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bmp");
    }

    private void ImageSaved(string path)
    {
        LastImage = path;
        ImageRequested = false;
        Console.WriteLine($"Frame image: {path}");
    }

    public void StartMeasurements(bool recordTrace = false)
    {
        if (IsMeasuring) return;
        if (Startup is null)
        {
            _recordTrace = recordTrace;
            _measureAfterStartup = true;
            return;
        }
        _results.Clear();
        _frameWindows.Clear();
        _measurementError = null;
        _recordTrace = recordTrace;
        _interactiveOptions = Options;
        _interactiveScene = SelectedScene;
        Options = Options with { WindowVSync = false, FixedFrameSeconds = 1f / 60f };
        _resultDirectory = Path.Combine(AppContext.BaseDirectory, "Performance", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(_resultDirectory);
        _sceneStarted = 0;
        _testStarted = Stopwatch.GetTimestamp();
        IsMeasuring = true;
        Application.MeasureInterface = true;
        Application.DebugUiEnabled = false;
        SelectScene(0);
    }

    internal void RecordStartup(ApplicationStartupTimes times)
    {
        Startup = times;
        if (!_measureAfterStartup) return;
        _measureAfterStartup = false;
        StartMeasurements(_recordTrace);
    }

    public void SelectScene(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Scenes.Count);
        SelectedScene = index;
        Options = Options with { DynamicScene = Current.Animate };
        Application.LoadScene(Current.ContentDirectory);
    }

    private void BeginScene(long started)
    {
        if (!IsMeasuring) return;
        _sceneTimeLimit = (45 - Stopwatch.GetElapsedTime(_testStarted, started).TotalSeconds) / (Scenes.Count - SelectedScene);
        _sceneStarted = started;
        _sampleStarted = 0;
        _renderingStarted = 0;
        _renderedFrames = 0;
        _streamingFrames = 0;
        _streamingMs = 0;
        _streamingMaxFrameMs = 0;
        _wasStreaming = false;
        _firstFrameMs = 0;
        _readyFrames = 0;
        _frames.Clear();
        _blendFrames.Clear();
        _shadowResolutionFeedback.Clear();
        _firstSampleRenderedFrame = 0;
        _fixedWarmupComplete = false;
        _fixedWindowError = null;
        _sceneSampleFinished = false;
        if (_recordTrace)
            _trace = EngineTrace.Start(Path.Combine(_resultDirectory!, $"scene-{SelectedScene}.setrace"),
                new SomeEngine.Diagnostics.TraceOptions
                {
                    Detail = Options.TraceDetail, Allocations = Options.TraceAllocations,
                    Metadata = new Dictionary<string, string> { ["scene"] = Current.Name },
                });
    }

    private void RecordBlendFrame(BlendFrameMetrics frame)
    {
        if (IsMeasuring) _blendFrames[frame.SimulationFrame] = frame;
    }

    private IEnumerable<IApplicationFrameFeature> CreateMeasurementFeatures(ApplicationRendering rendering)
    {
        if (rendering.Shadows is { } shadows) yield return new ShadowResolutionMeasurements(shadows, this);
        if (Options.MeasureBlending) yield return new BlendMeasurementFeature(rendering, RecordBlendFrame);
    }

    private void RecordShadowResolutionBias(ulong frame, float lodBias)
    {
        if (IsMeasuring && _sampleStarted != 0 && !ReloadRequested)
            _shadowResolutionFeedback.Add((frame, lodBias));
    }

    private sealed class ShadowResolutionMeasurements : IApplicationFrameFeature
    {
        private readonly VirtualShadowFeature _shadows;
        private readonly SceneTestPlatform _platform;

        internal ShadowResolutionMeasurements(VirtualShadowFeature shadows, SceneTestPlatform platform)
        {
            (_shadows, _platform) = (shadows, platform);
            shadows.ResolutionBiasCompleted += platform.RecordShadowResolutionBias;
            Reconcile();
        }

        public void Reconcile() => _platform._shadowDynamicResolutionEnabled = _shadows.Settings.DynamicResolutionEnabled;
        public void Record(ref RenderGraphFrame frame, int simulationFrame) { }
        public void Dispose() => _shadows.ResolutionBiasCompleted -= _platform.RecordShadowResolutionBias;
    }

    private void RecordFrame(ApplicationFrameTimes frame, ClusterRenderDiagnostics diagnostics)
    {
        if (!IsMeasuring) return;
        if (_renderingStarted == 0 && diagnostics.Meshes.PublishedMeshes == diagnostics.Meshes.RegisteredMeshes)
        {
            _renderingStarted = Stopwatch.GetTimestamp();
            _firstFrameMs = Stopwatch.GetElapsedTime(_sceneStarted).TotalMilliseconds;
        }
        _renderedFrames++;
        var residency = diagnostics.Residency;
        bool ready = diagnostics.Meshes.PublishedMeshes == diagnostics.Meshes.RegisteredMeshes &&
            residency.QueuedPageLoads == 0 && residency.ActivePageLoads == 0 && residency.PageLoadFailures == 0 &&
            !residency.HasChanges && residency.UploadingPages == 0;
        // Include the frame that submits the final uploads. Its completed CPU
        // diagnostics can already report ready, while the frame still paid for
        // those uploads and the resulting graph and command work.
        if (_renderingStarted != 0 && (!ready || _wasStreaming))
        {
            _streamingFrames = _renderedFrames;
            _streamingMs = Stopwatch.GetElapsedTime(_renderingStarted).TotalMilliseconds;
            if (_renderedFrames > 1)
                _streamingMaxFrameMs = Math.Max(_streamingMaxFrameMs, frame.ElapsedMs);
        }
        _wasStreaming = !ready;
        _readyFrames = ready ? _readyFrames + 1 : 0;
        double elapsed = Stopwatch.GetElapsedTime(_sceneStarted).TotalSeconds;
        bool warmupReady = _readyFrames >= 32 && _renderedFrames >= 128 &&
            Stopwatch.GetElapsedTime(_renderingStarted).TotalSeconds >= 3;
        bool sampled;
        bool finishedWindow = false;
        if (Current.MeasurementFrames is { } requested)
        {
            var selection = requested.SelectFrame(frame.SimulationFrame, _frames.Count, _fixedWarmupComplete, ready);
            if (frame.SimulationFrame < requested.FirstFrame) _fixedWarmupComplete = warmupReady;
            _fixedWindowError ??= selection.Error;
            _measurementError ??= selection.Error;
            if (selection.Collect) CollectFrame();
            finishedWindow = selection.Finished;
            sampled = finishedWindow && _frames.Count == requested.FrameCount &&
                _fixedWindowError is null && _measurementError is null;
        }
        else
        {
            if (warmupReady) CollectFrame();
            else
            {
                _sampleStarted = 0;
                _firstSampleRenderedFrame = 0;
                _frames.Clear();
                _shadowResolutionFeedback.Clear();
            }
            sampled = _sampleStarted != 0 && Stopwatch.GetElapsedTime(_sampleStarted).TotalSeconds >= 2;
        }
        bool expired = elapsed >= _sceneTimeLimit || Stopwatch.GetElapsedTime(_testStarted).TotalSeconds >= 45;
        if (!sampled && !expired && !finishedWindow) return;
        if (Current.MeasurementFrames is not null && !sampled)
        {
            _fixedWindowError ??= "The requested simulation-frame window did not complete before measurement ended.";
            _measurementError ??= _fixedWindowError;
        }
        string status = sampled ? "Measured" : Current.MeasurementFrames is not null
            ? "Incomplete" : "Not converged within time limit";
        SceneFrameWindow sampleWindow = SaveFrameWindow(status, _fixedWindowError);
        double[] times = _frames.Select(f => f.ElapsedMs).Order().ToArray();
        _results.Add(new(Current.Name, status, times.Length,
            times.Length == 0 ? null : times.Average(),
            times.Length == 0 ? null : times[times.Length / 2],
            times.Length == 0 ? null : times[(int)((times.Length - 1) * 0.95)],
            _frames.Count == 0 ? null : _frames.Average(f => f.PrepareMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.ReconcileMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.ExecuteMs),
            residency.ResidentPages, residency.RegisteredPages, residency.QueuedPageLoads, residency.ActivePageLoads,
            residency.PageHeapUsedBytes, residency.PageHeapFreeBytes, residency.BackpressuredPages,
            _renderWidth, _renderHeight, Options.DynamicScene,
            _firstFrameMs, _streamingFrames, _streamingMs, _streamingMaxFrameMs, residency.DroppedPageFaults,
            ShadowResolutionSummary.FromFeedback(_shadowDynamicResolutionEnabled,
                CollectionsMarshal.AsSpan(_shadowResolutionFeedback)),
            _frames.Sum(f => (long)f.Views.RebuildCount),
            _frames.Count == 0 ? null : _frames.Average(f => f.Views.RebuildCount),
            _frames.Sum(f => (long)f.Views.ComputationCount),
            _frames.Count == 0 ? null : _frames.Average(f => f.Views.ComputationCount),
            _frames.Count == 0 ? null : _frames.Average(f => f.Views.RebuildMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.Views.TextMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.Views.LayoutMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.Views.TotalMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.ExtractionMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.Views.AllocatedBytes),
            _frames.Count == 0 ? null : _frames.Average(f => f.ExtractionAllocatedBytes),
            _frames.Count == 0 ? null : _frames.Average(f => f.Views.AnimationMs))
        {
            RequestedSimulationWindow = sampleWindow.RequestedSimulationWindow,
            FirstSimulationFrame = sampleWindow.FirstSimulationFrame,
            LastSimulationFrame = sampleWindow.LastSimulationFrame,
            SampleWallSeconds = sampleWindow.SampleWallSeconds,
        });
        Application.LoadScene(Current.ContentDirectory);

        void CollectFrame()
        {
            if (_sampleStarted == 0)
            {
                _sampleStarted = Stopwatch.GetTimestamp();
                _firstSampleRenderedFrame = _renderedFrames;
            }
            _frames.Add(frame);
        }
    }

    private SceneFrameWindow SaveFrameWindow(string status, string? error)
    {
        var window = new SceneFrameWindow(Current.Name, _firstSampleRenderedFrame,
            Options.FixedFrameSeconds, _frames.ToArray(), Current.MeasurementFrames,
            _frames.Count == 0 ? null : _frames[0].SimulationFrame,
            _frames.Count == 0 ? null : _frames[^1].SimulationFrame,
            _sampleStarted == 0 ? null : Stopwatch.GetElapsedTime(_sampleStarted).TotalSeconds,
            status, error);
        _frameWindows.Add(window);
        _sceneSampleFinished = true;
        return window;
    }

    internal void EndScene(string? error = null)
    {
        _trace?.Dispose();
        _trace = null;
        if (!IsMeasuring || _sceneStarted == 0) return;
        error ??= _measurementError;
        if (Current.MeasurementFrames is not null && !_sceneSampleFinished)
        {
            error ??= "The requested simulation-frame window was interrupted.";
            SaveFrameWindow("Incomplete", error);
        }
        if (MeasureBlending && _sceneSampleFinished)
        {
            SceneFrameWindow window = _frameWindows[^1];
            BlendFrameMetrics[] gpu = window.Frames
                .Where(frame => _blendFrames.ContainsKey(frame.SimulationFrame))
                .Select(frame => _blendFrames[frame.SimulationFrame]).ToArray();
            _frameWindows[^1] = window with { BlendFrames = gpu };
            if (_results.Count > 0) _results[^1] = _results[^1] with { Blending = SceneBlendTiming.FromFrames(gpu) };
            if (gpu.Length != window.Frames.Length)
                error ??= "The GPU blend samples did not cover the complete scene-frame window.";
        }
        if (error is null && ReloadRequested && SelectedScene + 1 < Scenes.Count && Stopwatch.GetElapsedTime(_testStarted).TotalSeconds < 45)
        {
            SelectScene(SelectedScene + 1);
            return;
        }
        var frameSamples = new JsonObject
        {
            ["SchemaVersion"] = 3,
            ["TimingScopes"] = FrameTimingScopes,
            ["BlendTimingSchemaVersion"] = MeasureBlending ? 2 : null,
            ["BlendTimingScopes"] = MeasureBlending ? BlendTimingScopes : null,
            ["Sampling"] = "Default warmup and wall-clock samples, or the configured zero-based simulation-frame interval.",
            ["Scenes"] = JsonSerializer.SerializeToNode(_frameWindows, SceneJsonContext.Default.ListSceneFrameWindow),
        };
        File.WriteAllText(Path.Combine(_resultDirectory!, "frame-samples.json"),
            frameSamples.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        LastReport = Path.Combine(_resultDirectory!, "results.json");
        var report = new JsonObject
        {
            ["Complete"] = error is null && _results.Count == Scenes.Count && _results.All(result => result.Status == "Measured"),
            ["CapturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["FrameTimingSchemaVersion"] = 3,
            ["TimingScopes"] = FrameTimingScopes,
            ["GraphicsDevice"] = _graphicsDevice,
            ["GraphicsDriver"] = _graphicsDriver,
            ["Processor"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["NativeAot"] = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
            ["TieredCompilation"] = AppContext.GetData("System.Runtime.TieredCompilation")?.ToString(),
            ["TieredCompilationOverride"] = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")
                ?? Environment.GetEnvironmentVariable("COMPlus_TieredCompilation"),
            ["Error"] = error,
            ["ExpectedScenes"] = new JsonArray(Scenes.Select(scene => (JsonNode?)JsonValue.Create(scene.Name)).ToArray()),
            ["ElapsedSeconds"] = Stopwatch.GetElapsedTime(_testStarted).TotalSeconds,
            ["Timing"] = MeasureBlending
                ? "Presented frame elapsed time, CPU stage times and completed GPU blend phase times"
                : "Presented frame elapsed time and CPU-side stage elapsed time",
            ["BlendTimingSchemaVersion"] = MeasureBlending ? 2 : null,
            ["BlendTimingScopes"] = MeasureBlending ? BlendTimingScopes : null,
            ["TraceEnabled"] = _recordTrace,
            ["ShadowResolutionSampling"] = "Completed asynchronous bias feedback received during the timed sample window; feedback may lag presented frames. No end-of-window GPU wait. Empty feedback has null bias statistics.",
            ["Startup"] = JsonSerializer.SerializeToNode(Startup, SceneJsonContext.Default.ApplicationStartupTimes),
            ["TraceDetail"] = Options.TraceDetail,
            ["TraceAllocations"] = Options.TraceAllocations,
            ["TemporalAntialiasing"] = Options.TemporalAntialiasing,
            ["VirtualShadows"] = Options.VirtualShadows,
            ["DirectionalDynamicReceiverRefresh"] = Options.DirectionalDynamicReceiverRefresh,
            ["AsyncCompute"] = Options.AsyncCompute,
            ["ForceHardwareRaster"] = Options.ForceHardwareRaster,
            ["UseInstanceGroups"] = Options.UseInstanceGroups,
            ["FixedFrameSeconds"] = Options.FixedFrameSeconds,
            ["SynchronousFrames"] = Options.SynchronousFrames,
            ["ScenePlayback"] = Options.ScenePlayback,
            ["DisableMeshShaders"] = Options.DisableMeshShaders,
            ["DeviceValidation"] = Options.DeviceValidation,
            ["GeometryMemoryMegabytes"] = GeometryMemoryMegabytes,
            ["ColorFeature"] = ColorFeatureSettings is { } settings
                ? new JsonObject(settings.Select(static pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))) : null,
            ["Scenes"] = JsonSerializer.SerializeToNode(_results, SceneJsonContext.Default.ListSceneTiming),
        };
        File.WriteAllText(LastReport, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Performance report: {LastReport}");
        IsMeasuring = false;
        Application.MeasureInterface = false;
        Application.DebugUiEnabled = true;
        Options = _interactiveOptions!;
        SelectedScene = _interactiveScene;
        Application.ContentDirectory = Current.ContentDirectory;
        _interactiveOptions = null;
    }

    internal void Draw()
    {
        ImGui.SeparatorText("Test scenes");
        ImGui.TextUnformatted("F2: spheres   F3: Sponza   F4: Amazon Bistro");
        if (ImGui.BeginCombo("Scene", Current.Name))
        {
            for (int index = 0; index < Scenes.Count; index++)
            {
                TestScene scene = Scenes[index];
                bool available = File.Exists(Path.Combine(scene.ContentDirectory, "Library", "AssetManifest", "catalog.json"));
                ImGui.BeginDisabled(!available);
                if (ImGui.Selectable(available ? scene.Name : scene.Name + " (build content first)", index == SelectedScene))
                    SelectScene(index);
                ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }
        if (ImGui.Button("Reset scene and camera")) SelectScene(SelectedScene);
        ImGui.SameLine();
        if (ImGui.Button("Capture image (F8)")) CaptureImage();
        if (ImGui.Button("Measure all scenes (F9)")) StartMeasurements();
        ImGui.SameLine();
        if (ImGui.Button("Trace all (F10)")) StartMeasurements(recordTrace: true);
        bool detailedTrace = Options.TraceDetail;
        bool allocationTrace = Options.TraceAllocations;
        bool traceChanged = ImGui.Checkbox("Detailed trace", ref detailedTrace);
        ImGui.SameLine();
        traceChanged |= ImGui.Checkbox("Allocation trace", ref allocationTrace);
        if (traceChanged) Options = Options with { TraceDetail = detailedTrace, TraceAllocations = allocationTrace };
        foreach (SceneTiming result in _results)
            ImGui.TextUnformatted($"{result.Scene}: {result.MedianMs:F3} ms / P95 {result.P95Ms:F3} ms ({result.Status})");
        if (LastReport is not null) ImGui.TextWrapped(LastReport);
        int geometryMemory = GeometryMemoryMegabytes;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Geometry memory (MiB)", ref geometryMemory, 64, 512))
        {
            GeometryMemoryMegabytes = geometryMemory;
            Application.Reload();
        }
        bool taa = Options.TemporalAntialiasing;
        bool shadows = Options.VirtualShadows;
        bool asyncCompute = Options.AsyncCompute;
        bool hardwareRaster = Options.ForceHardwareRaster;
        bool groups = Options.UseInstanceGroups;
        bool vsync = Options.WindowVSync;
        bool meshShaders = !Options.DisableMeshShaders;
        bool synchronous = Options.SynchronousFrames;
        bool validation = Options.DeviceValidation;
        string[] playbacks = ["Scene behavior", "Motion and lights", "Material changes", "Camera sweep"];
        int playback = Options.ScenePlayback switch { "motion" => 1, "materials" => 2, "camera" => 3, _ => 0 };
        bool changed = ImGui.Checkbox("Temporal antialiasing", ref taa);
        changed |= ImGui.Checkbox("Virtual shadows", ref shadows);
        changed |= ImGui.Checkbox("Async compute", ref asyncCompute);
        changed |= ImGui.Checkbox("Hardware raster only", ref hardwareRaster);
        changed |= ImGui.Checkbox("Instance groups", ref groups);
        changed |= ImGui.Checkbox("VSync", ref vsync);
        changed |= ImGui.Checkbox("Mesh shaders", ref meshShaders);
        changed |= ImGui.Checkbox("Wait for GPU each frame", ref synchronous);
        changed |= ImGui.Checkbox("Graphics validation", ref validation);
        changed |= ImGui.Combo("Playback", ref playback, playbacks, playbacks.Length);
        if (changed)
        {
            Options = Options with
            {
                TemporalAntialiasing = taa, VirtualShadows = shadows, AsyncCompute = asyncCompute,
                ForceHardwareRaster = hardwareRaster, UseInstanceGroups = groups, WindowVSync = vsync,
                DisableMeshShaders = !meshShaders, SynchronousFrames = synchronous, DeviceValidation = validation,
                ScenePlayback = playback switch { 1 => "motion", 2 => "materials", 3 => "camera", _ => null },
            };
            Application.Reload();
        }
    }

    internal sealed record SceneTiming(string Scene, string Status, int Frames, double? MeanMs, double? MedianMs,
        double? P95Ms, double? PrepareMs, double? ReconcileMs, double? ExecuteMs,
        uint ResidentPages, uint RegisteredPages, int QueuedLoads, int ActiveLoads,
        uint GeometryUsedBytes, uint GeometryFreeBytes, ulong BackpressuredPages,
        int RenderWidth, int RenderHeight, bool AnimateScene,
        double FirstFrameMs, int StreamingFrames, double StreamingMs, double StreamingMaxFrameMs, ulong DroppedPageFaults,
        ShadowResolutionSummary ShadowResolution,
        long Rebuilds, double? RebuildsPerFrame, long Computations, double? ComputationsPerFrame,
        double? RebuildMs, double? TextMs, double? LayoutMs, double? ViewMs, double? ExtractionMs,
        double? ViewAllocatedBytes, double? ExtractionAllocatedBytes, double? AnimationMs)
    {
        public SceneBlendTiming? Blending { get; init; }
        public SimulationFrameWindow? RequestedSimulationWindow { get; init; }
        public int? FirstSimulationFrame { get; init; }
        public int? LastSimulationFrame { get; init; }
        public double? SampleWallSeconds { get; init; }
    }

    internal sealed record SceneFrameWindow(
        string Scene, int FirstRenderedFrame, float FixedFrameSeconds, ApplicationFrameTimes[] Frames,
        SimulationFrameWindow? RequestedSimulationWindow, int? FirstSimulationFrame, int? LastSimulationFrame,
        double? SampleWallSeconds, string Status, string? Error)
    {
        public BlendFrameMetrics[]? BlendFrames { get; init; }
    }

    internal sealed record ShadowResolutionSummary(
        bool Enabled, int CompletedFeedbacks, ulong? FirstFeedbackFrame, ulong? LastFeedbackFrame,
        float? MinimumLodBias, float? MaximumLodBias, double? MeanLodBias, float? LastLodBias)
    {
        internal static ShadowResolutionSummary FromFeedback(
            bool enabled, ReadOnlySpan<(ulong Frame, float LodBias)> feedback)
        {
            if (feedback.IsEmpty) return new(enabled, 0, null, null, null, null, null, null);
            float minimum = feedback[0].LodBias;
            float maximum = minimum;
            double sum = 0;
            foreach (var sample in feedback)
            {
                minimum = Math.Min(minimum, sample.LodBias);
                maximum = Math.Max(maximum, sample.LodBias);
                sum += sample.LodBias;
            }
            return new(enabled, feedback.Length, feedback[0].Frame, feedback[^1].Frame,
                minimum, maximum, sum / feedback.Length, feedback[^1].LodBias);
        }
    }

    private const string BlendTimingScopes =
        "GPU milliseconds on the graphics queue, matched to the recorded SimulationFrame and SubmittedFrame. " +
        "LayerVisibility includes tile lists and visibility raster with inline shadow-page marking; " +
        "LayerClassification bins stored fragments; LayerShading evaluates the user materials; " +
        "LayerComposite sorts and composes complete pixel fragment sets. " +
        "OpaqueRequests measures depth-based page requests; OpaqueShading measures opaque material evaluation. " +
        "DecalBinning measures the independent decal grid. " +
        "RecordBytes and PayloadBytes count stored fragments. BlockPoolPeakBytes counts used complete blocks. " +
        "FragmentStorage bytes cover block records and headers, payloads, tile heads and frame counters; " +
        "the other layer work buffers, shader parameters, textures, mesh residency and instance storage retain their own accounting. " +
        "Decal bytes cover the feature's independent grid, volumes, user data and retained references.";

    internal sealed record SceneBlendTiming(
        int Frames,
        GpuPhaseTiming LayerVisibility, GpuPhaseTiming LayerClassification,
        GpuPhaseTiming LayerShading, GpuPhaseTiming LayerComposite,
        GpuPhaseTiming OpaqueRequests, GpuPhaseTiming OpaqueShading, GpuPhaseTiming DecalBinning,
        double MeanFragments, uint PeakFragmentsPerPixel, uint PeakBlocksUsed,
        uint PeakFragmentOverflow, uint PeakSplitTiles, uint PeakSlowPathPixels,
        double MeanRecordBytes, double MeanPayloadBytes,
        ulong PeakRecordBytes, ulong PeakPayloadBytes, ulong PeakBlockPoolBytes, ulong PeakBlockPoolCapacityBytes,
        double MeanFragmentStorageUsedBytes, ulong PeakFragmentStorageUsedBytes,
        ulong PeakFragmentStorageCapacityBytes, ulong PeakFragmentStorageAllocationBytes,
        int DecalBuildFrames, int DecalReuseFrames,
        double MeanDecalVolumes, double MeanDecalReferences, double MeanDecalRetainedReferences,
        double MeanDecalUsedBytes, ulong PeakDecalUsedBytes, ulong PeakDecalCapacityBytes, ulong PeakDecalAllocationBytes)
    {
        internal static SceneBlendTiming? FromFrames(BlendFrameMetrics[] frames)
        {
            if (frames.Length == 0) return null;
            return new(frames.Length,
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.LayerVisibilityMs)),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.LayerClassificationMs)),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.LayerShadingMs)),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.LayerCompositeMs)),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.OpaqueRequestsMs)),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.OpaqueShadingMs)),
                GpuPhaseTiming.FromValues(frames.Select(frame => frame.DecalBinningMs)),
                frames.Average(frame => frame.Layers.Fragments), frames.Max(frame => frame.Layers.MaxFragmentsPerPixel),
                frames.Max(frame => frame.Layers.BlocksUsed), frames.Max(frame => frame.Layers.FragmentOverflow),
                frames.Max(frame => frame.Layers.SplitTiles), frames.Max(frame => frame.Layers.SlowPathPixels),
                frames.Average(frame => (double)frame.RecordBytes), frames.Average(frame => (double)frame.PayloadBytes),
                frames.Max(frame => frame.RecordBytes), frames.Max(frame => frame.PayloadBytes),
                frames.Max(frame => frame.BlockPoolPeakBytes), frames.Max(frame => frame.BlockPoolCapacityBytes),
                frames.Average(frame => (double)frame.FragmentStorageUsedBytes), frames.Max(frame => frame.FragmentStorageUsedBytes),
                frames.Max(frame => frame.FragmentStorageCapacityBytes), frames.Max(frame => frame.FragmentStorageAllocationBytes),
                frames.Count(frame => !frame.ReusedDecalBinning), frames.Count(frame => frame.ReusedDecalBinning),
                frames.Average(frame => frame.DecalVolumes), frames.Average(frame => frame.DecalReferences),
                frames.Average(frame => frame.DecalRetainedReferences), frames.Average(frame => (double)frame.DecalUsedBytes),
                frames.Max(frame => frame.DecalUsedBytes), frames.Max(frame => frame.DecalCapacityBytes),
                frames.Max(frame => frame.DecalAllocationBytes));
        }
    }

    internal sealed record GpuPhaseTiming(double MeanMs, double MedianMs, double P95Ms)
    {
        internal static GpuPhaseTiming FromValues(IEnumerable<double> values)
        {
            double[] sorted = values.Order().ToArray();
            return new(sorted.Average(), sorted[sorted.Length / 2], sorted[(int)((sorted.Length - 1) * .95)]);
        }
    }
}

