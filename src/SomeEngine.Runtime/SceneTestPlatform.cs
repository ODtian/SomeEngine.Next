using ImGuiNET;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SomeEngine.Diagnostics;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Runtime;

public sealed record TestScene(string Name, string ContentDirectory, bool Animate = false)
{
    /// <summary>Optional identical simulation-frame interval for repeatable animated comparisons.</summary>
    public SimulationFrameWindow? MeasurementFrames { get; init; }
}

/// <summary>Shared scene selection and render settings for the interactive test application.</summary>
public sealed class SceneTestPlatform
{
    public static TestScene[] ReadScenes(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), SceneJsonContext.Default.TestSceneArray)!;

    public SceneTestPlatform(IReadOnlyList<TestScene> scenes, RuntimeStartupOptions? options = null)
    {
        if (scenes.Count == 0) throw new ArgumentException("At least one test scene is required.", nameof(scenes));
        Scenes = scenes.ToArray();
        Options = options ?? RuntimeStartupOptions.Parse([]) with
        {
            WindowVSync = false, DynamicScene = scenes[0].Animate, AsyncCompute = true,
        };
    }

    public IReadOnlyList<TestScene> Scenes { get; }
    public int SelectedScene { get; private set; }
    internal RuntimeStartupOptions Options { get; private set; }
    internal bool ReloadRequested { get; private set; }
    internal TestScene Current => Scenes[SelectedScene];
    private readonly List<SceneTiming> _results = [];
    private readonly List<FrameTiming> _frames = [];
    private const string FrameTimingScopes =
        "All timing fields are CPU wall-clock milliseconds. ElapsedMs retains the original whole-frame boundaries. " +
        "PrepareMs, ReconcileMs, ExecuteMs, GameUpdateWaitMs, FrameSlotAcquireMs, AcquireImageMs and PresentMs " +
        "are disjoint intervals within that frame. FrameSlotAcquireMs covers TryOpenNextFrame, including " +
        "completed-work collection, slot selection/waits and reset; it is not a pure GPU wait. " +
        "GraphExecuteMs and CompletionWaitMs are nested inside ExecuteMs, not additional frame costs. " +
        "GraphExecuteMs covers RenderGraphFrame.Execute (analysis, recording and submission). " +
        "CompletionWaitMs covers only an already-requested synchronous/output-readback completion wait. " +
        "OtherFrameMs is ElapsedMs minus the seven disjoint intervals, never minus the nested fields. " +
        "It includes remaining event/UI/setup/scheduling/cleanup work and is not GPU elapsed time.";
    private readonly List<(ulong Frame, float LodBias)> _shadowResolutionFeedback = [];
    private string? _measurementError;
    private readonly List<SceneFrameWindow> _frameWindows = [];
    private int _firstSampleRenderedFrame;
    private bool _fixedWarmupComplete;
    private string? _fixedWindowError;
    private bool _sceneSampleFinished;
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
    private RuntimeStartupOptions? _interactiveOptions;
    private int _interactiveScene;
    private ClusterShadingPath? _installedShadingPath;
    private uint _debugMode;
    private int _debugViewCount;
    private int _renderWidth;
    private int _renderHeight;
    private string? _graphicsDevice;
    private string? _graphicsDriver;
    public bool IsMeasuring { get; private set; }
    public string? LastReport { get; private set; }
    public string? StartupTraceOutput { get; init; }
    public ApplicationStartupTimes? Startup { get; private set; }
    public int GeometryMemoryMegabytes { get; private set; } = 256;
    internal bool ImageRequested { get; private set; }
    internal string? LastImage { get; private set; }

    internal void SetRenderingInfo(int width, int height, string device, string driver)
        => (_renderWidth, _renderHeight, _graphicsDevice, _graphicsDriver) = (width, height, device, driver);

    internal void SetShadingInfo(ClusterShadingPath path, uint debugMode, int debugViewCount)
        => (_installedShadingPath, _debugMode, _debugViewCount) = (path, debugMode, debugViewCount);

    public void CaptureImage() => ImageRequested = true;

    internal string ImageDestination()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Captures");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"scene-{SelectedScene}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bmp");
    }

    internal void ImageSaved(string path)
    {
        LastImage = path;
        ImageRequested = false;
        Console.WriteLine($"Frame image: {path}");
    }

    internal void SetViewOptions(ClusterShadingPath shadingPath, IReadOnlyList<DebugViewSettings> debugViews)
    {
        if (!IsMeasuring) Options = Options with { ShadingPath = shadingPath, DebugViews = debugViews.ToArray() };
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
        _measurementError = null;
        _frameWindows.Clear();
        _recordTrace = recordTrace;
        _interactiveOptions = Options;
        _interactiveScene = SelectedScene;
        Options = Options with { WindowVSync = false, FixedFrameSeconds = 1f / 60f, DebugViews = [] };
        _resultDirectory = Path.Combine(AppContext.BaseDirectory, "Performance", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(_resultDirectory);
        _sceneStarted = 0;
        _testStarted = Stopwatch.GetTimestamp();
        IsMeasuring = true;
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
        ReloadRequested = true;
    }

    internal void BeginScene()
    {
        ReloadRequested = false;
        if (!IsMeasuring) return;
        _sceneTimeLimit = (45 - Stopwatch.GetElapsedTime(_testStarted).TotalSeconds) / (Scenes.Count - SelectedScene);
        _sceneStarted = Stopwatch.GetTimestamp();
        _sampleStarted = 0;
        _firstSampleRenderedFrame = 0;
        _fixedWarmupComplete = false;
        _fixedWindowError = null;
        _sceneSampleFinished = false;
        _renderingStarted = 0;
        _renderedFrames = 0;
        _streamingFrames = 0;
        _streamingMs = 0;
        _streamingMaxFrameMs = 0;
        _wasStreaming = false;
        _firstFrameMs = 0;
        _readyFrames = 0;
        _frames.Clear();
        _shadowResolutionFeedback.Clear();
        if (_recordTrace)
            _trace = EngineTrace.Start(Path.Combine(_resultDirectory!, $"scene-{SelectedScene}.setrace"),
                new SomeEngine.Diagnostics.TraceOptions
                {
                    Detail = Options.TraceDetail, Allocations = Options.TraceAllocations,
                    Metadata = new Dictionary<string, string> { ["scene"] = Current.Name },
                });
    }

    internal void RecordShadowResolutionBias(ulong frame, float lodBias)
    {
        if (IsMeasuring && _sampleStarted != 0 && !ReloadRequested)
            _shadowResolutionFeedback.Add((frame, lodBias));
    }

    internal void InvalidateMeasurements(string reason)
    {
        if (IsMeasuring) _measurementError ??= reason;
    }

    internal void RecordFrame(FrameTiming frame, ClusterRenderDiagnostics diagnostics,
        bool shadowDynamicResolutionEnabled)
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
            !residency.HasPendingPublication && residency.PendingPageLoads == 0;
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
            if (selection.Error is not null) InvalidateMeasurements(selection.Error);
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
            InvalidateMeasurements(_fixedWindowError);
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
            ShadowResolutionSummary.FromFeedback(shadowDynamicResolutionEnabled,
                CollectionsMarshal.AsSpan(_shadowResolutionFeedback)),
            sampleWindow.RequestedSimulationWindow, sampleWindow.FirstSimulationFrame,
            sampleWindow.LastSimulationFrame, sampleWindow.SampleWallSeconds,
            _frames.Count == 0 ? null : _frames.Average(f => f.GameUpdateWaitMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.FrameSlotAcquireMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.AcquireImageMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.PresentMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.GraphExecuteMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.CompletionWaitMs),
            _frames.Count == 0 ? null : _frames.Average(f => f.OtherFrameMs)));
        ReloadRequested = true;

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
        if (error is null && ReloadRequested && SelectedScene + 1 < Scenes.Count && Stopwatch.GetElapsedTime(_testStarted).TotalSeconds < 45)
        {
            SelectScene(SelectedScene + 1);
            return;
        }
        var frameSamples = new JsonObject
        {
            ["SchemaVersion"] = 3,
            ["TimingScopes"] = FrameTimingScopes,
            ["Sampling"] = "Default: original warmup and wall-clock sample. Explicit MeasurementFrames: the requested zero-based simulation frames, without retiming or replacing failed frames. Each sample records its SimulationFrame.",
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
            ["Timing"] = "Presented frame elapsed time and CPU-side stage elapsed time; GPU duration is not measured",
            ["TraceEnabled"] = _recordTrace,
            ["ShadowResolutionSampling"] = "Completed asynchronous bias feedback received during the timed sample window; feedback may lag presented frames. No end-of-window GPU wait. Empty feedback has null bias statistics.",
            ["FrameSamples"] = "frame-samples.json",
            ["Startup"] = JsonSerializer.SerializeToNode(Startup, SceneJsonContext.Default.ApplicationStartupTimes),
            ["TraceDetail"] = Options.TraceDetail,
            ["TraceAllocations"] = Options.TraceAllocations,
            ["ShadingPath"] = _installedShadingPath?.ToString(),
            ["DebugMode"] = _debugMode,
            ["DebugViewCount"] = _debugViewCount,
            ["TemporalAntialiasing"] = Options.TemporalAntialiasing,
            ["VirtualShadows"] = Options.VirtualShadows,
            ["AsyncCompute"] = Options.AsyncCompute,
            ["ForceHardwareRaster"] = Options.ForceHardwareRaster,
            ["UseInstanceGroups"] = Options.UseInstanceGroups,
            ["FixedFrameSeconds"] = Options.FixedFrameSeconds,
            ["SynchronousFrames"] = Options.SynchronousFrames,
            ["ScenePlayback"] = Options.ScenePlayback,
            ["DisableMeshShaders"] = Options.DisableMeshShaders,
            ["DeviceValidation"] = Options.DeviceValidation,
            ["GeometryMemoryMegabytes"] = GeometryMemoryMegabytes,
            ["Scenes"] = JsonSerializer.SerializeToNode(_results, SceneJsonContext.Default.ListSceneTiming),
        };
        File.WriteAllText(LastReport, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Performance report: {LastReport}");
        IsMeasuring = false;
        Options = _interactiveOptions!;
        SelectedScene = _interactiveScene;
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
            ReloadRequested = true;
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
            ReloadRequested = true;
        }
    }

    internal readonly record struct FrameTiming(double ElapsedMs, double PrepareMs, double ReconcileMs, double ExecuteMs,
        int SimulationFrame, double GameUpdateWaitMs, double FrameSlotAcquireMs, double AcquireImageMs, double PresentMs,
        double GraphExecuteMs, double CompletionWaitMs)
    {
        public double OtherFrameMs => ElapsedMs - PrepareMs - ReconcileMs - ExecuteMs - GameUpdateWaitMs -
            FrameSlotAcquireMs - AcquireImageMs - PresentMs;
    }
    internal sealed record SceneTiming(string Scene, string Status, int Frames, double? MeanMs, double? MedianMs,
        double? P95Ms, double? PrepareMs, double? ReconcileMs, double? ExecuteMs,
        uint ResidentPages, uint RegisteredPages, int QueuedLoads, int ActiveLoads,
        uint GeometryUsedBytes, uint GeometryFreeBytes, ulong BackpressuredPages,
        int RenderWidth, int RenderHeight, bool AnimateScene,
        double FirstFrameMs, int StreamingFrames, double StreamingMs, double StreamingMaxFrameMs, ulong DroppedPageFaults,
        ShadowResolutionSummary ShadowResolution, SimulationFrameWindow? RequestedSimulationWindow,
        int? FirstSimulationFrame, int? LastSimulationFrame, double? SampleWallSeconds,
        double? GameUpdateWaitMs, double? FrameSlotAcquireMs, double? AcquireImageMs, double? PresentMs,
        double? GraphExecuteMs, double? CompletionWaitMs, double? OtherFrameMs);
    internal sealed record SceneFrameWindow(
        string Scene, int FirstRenderedFrame, float FixedFrameSeconds, FrameTiming[] Frames,
        SimulationFrameWindow? RequestedSimulationWindow, int? FirstSimulationFrame, int? LastSimulationFrame,
        double? SampleWallSeconds, string Status, string? Error);

}

