using ImGuiNET;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SomeEngine.Diagnostics;
using SomeEngine.Render.Cluster;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Runtime;

public sealed record TestScene(string Name, string ContentDirectory, bool Animate = false)
{
    /// <summary>Creates the scene's game callbacks for each load.</summary>
    [JsonIgnore]
    public Func<string, ApplicationScene>? CreateApplication { get; init; }
}

/// <summary>Shared scene selection and render settings for the interactive test application.</summary>
public sealed class SceneTestPlatform
{
    private const int MinimumReadyFrames = 16;
    private const int MinimumRenderedFrames = 16;
    private const double WarmupSeconds = 3;
    private const double SampleSeconds = 2;
    private const double MaximumTestSeconds = 45;
    public SceneTestPlatform(IReadOnlyList<TestScene> scenes, RuntimeStartupOptions? options = null)
    {
        if (scenes.Count == 0) throw new ArgumentException("At least one test scene is required.", nameof(scenes));
        Scenes = scenes.ToArray();
        Options = (options ?? RuntimeStartupOptions.Parse([]) with
        {
            WindowVSync = false, AsyncCompute = true, SurfacePanel = true, WorldNameplates = true,
        }) with { DynamicScene = scenes[0].Animate };
    }

    public IReadOnlyList<TestScene> Scenes { get; }
    public int SelectedScene { get; private set; }
    internal RuntimeStartupOptions Options { get; private set; }
    internal bool ReloadRequested { get; private set; }
    internal TestScene Current => Scenes[SelectedScene];
    private readonly List<SceneTiming> _results = [];
    private readonly List<FrameTiming> _frames = [];
    private readonly Dictionary<int, ApplicationFrameTiming> _completedFrames = [];
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
    private string? _resultDirectory;
    private RuntimeStartupOptions? _interactiveOptions;
    private int _interactiveScene;
    private int _renderWidth;
    private int _renderHeight;
    private string? _graphicsDevice;
    private string? _graphicsDriver;
    public bool IsMeasuring { get; private set; }
    public bool ExitAfterMeasurements { get; init; }
    public string? LastReport { get; private set; }
    public int GeometryMemoryMegabytes { get; private set; } = 256;
    public bool HudOverlayEnabled => Options.HudOverlay;
    public bool HudNumbersChange => Options.HudNumbersChange;
    internal bool ImageRequested { get; private set; }
    internal string? LastImage { get; private set; }

    internal void SetRenderingInfo(int width, int height, string device, string driver)
        => (_renderWidth, _renderHeight, _graphicsDevice, _graphicsDriver) = (width, height, device, driver);

    public void CaptureImage() => ImageRequested = true;

    /// <summary>Selects the standard HUD workload; static numbers provide a matching dependency-check measurement.</summary>
    public void SetHudOverlay(bool enabled, bool changeNumbers = true)
    {
        DevelopmentCheck.Require(!IsMeasuring, "Change the HUD between scene measurements.");
        if (Options.HudOverlay == enabled && Options.HudNumbersChange == changeNumbers) return;
        Options = Options with { HudOverlay = enabled, HudNumbersChange = changeNumbers };
        ReloadRequested = true;
    }

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

    public void StartMeasurements(bool recordTrace = false)
    {
        if (IsMeasuring) return;
        _results.Clear();
        _recordTrace = recordTrace;
        _interactiveOptions = Options;
        _interactiveScene = SelectedScene;
        Options = Options with { WindowVSync = false, FixedFrameSeconds = 1f / 60f };
        string workload = Options.HudOverlay ? (Options.HudNumbersChange ? "hud" : "hud-static") : "scene";
        _resultDirectory = Path.Combine(AppContext.BaseDirectory, "Performance", recordTrace ? "trace" : "timing",
            $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{workload}");
        Directory.CreateDirectory(_resultDirectory);
        _sceneStarted = 0;
        _testStarted = Stopwatch.GetTimestamp();
        IsMeasuring = true;
        SelectScene(0);
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
        _sceneTimeLimit = (MaximumTestSeconds - Stopwatch.GetElapsedTime(_testStarted).TotalSeconds) / (Scenes.Count - SelectedScene);
        _sceneStarted = Stopwatch.GetTimestamp();
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
        _completedFrames.Clear();
        if (_recordTrace)
            _trace = EngineTrace.Start(Path.Combine(_resultDirectory!, $"scene-{SelectedScene}.setrace"),
                new SomeEngine.Diagnostics.TraceOptions
                {
                    Detail = Options.TraceDetail, Allocations = Options.TraceAllocations,
                    Metadata = new Dictionary<string, string>
                    {
                        ["scene"] = Current.Name,
                        ["hudOverlay"] = Options.HudOverlay.ToString(),
                        ["hudNumbersChange"] = Options.HudNumbersChange.ToString(),
                    },
                });
    }

    internal void RecordFrame(FrameTiming frame, ClusterRenderDiagnostics diagnostics)
    {
        if (!IsMeasuring) return;
        if (_renderingStarted == 0)
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
        if (!ready || _wasStreaming)
        {
            _streamingFrames = _renderedFrames;
            _streamingMs = Stopwatch.GetElapsedTime(_renderingStarted).TotalMilliseconds;
            if (_renderedFrames > 1)
                _streamingMaxFrameMs = Math.Max(_streamingMaxFrameMs, frame.ElapsedMs);
        }
        _wasStreaming = !ready;
        _readyFrames = ready ? _readyFrames + 1 : 0;
        double elapsed = Stopwatch.GetElapsedTime(_sceneStarted).TotalSeconds;
        if (_readyFrames >= MinimumReadyFrames && _renderedFrames >= MinimumRenderedFrames &&
            Stopwatch.GetElapsedTime(_renderingStarted).TotalSeconds >= WarmupSeconds)
        {
            if (_sampleStarted == 0) _sampleStarted = Stopwatch.GetTimestamp();
            _frames.Add(frame);
        }
        else
        {
            _sampleStarted = 0;
            _frames.Clear();
        }
        bool sampled = _sampleStarted != 0 && Stopwatch.GetElapsedTime(_sampleStarted).TotalSeconds >= SampleSeconds;
        bool expired = elapsed >= _sceneTimeLimit || Stopwatch.GetElapsedTime(_testStarted).TotalSeconds >= MaximumTestSeconds;
        if (!sampled && !expired) return;
        double[] times = _frames.Select(f => f.ElapsedMs).Order().ToArray();
        _results.Add(new(Current.Name, sampled ? "Measured" : "Not converged within time limit", times.Length,
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
            _frames.Count == 0 ? null : _frames.Average(f => f.Views.AnimationMs)));
        ReloadRequested = true;
    }

    internal void RecordCompletedFrame(ApplicationFrameTiming timing)
    {
        if (IsMeasuring) _completedFrames.Add(timing.Frame, timing);
    }

    internal void EndScene(string? error = null, object? workload = null)
    {
        _trace?.Dispose();
        _trace = null;
        if (!IsMeasuring || _sceneStarted == 0) return;
        if (_results.Count != 0 && _results[^1].Scene == Current.Name)
            _results[^1] = _results[^1] with { Costs = _frames.Count == 0 ? null : Summarize(), Workload = workload };
        if (error is null && ReloadRequested && SelectedScene + 1 < Scenes.Count &&
            Stopwatch.GetElapsedTime(_testStarted).TotalSeconds < MaximumTestSeconds)
        {
            SelectScene(SelectedScene + 1);
            return;
        }
        LastReport = Path.Combine(_resultDirectory!, "results.json");
        File.WriteAllText(LastReport, JsonSerializer.Serialize(new
        {
            Complete = error is null && _results.Count == Scenes.Count && _results.All(result => result.Status == "Measured"),
            CapturedAtUtc = DateTimeOffset.UtcNow,
            GraphicsDevice = _graphicsDevice,
            GraphicsDriver = _graphicsDriver,
            GraphicsBackend = Options.GraphicsBackend.ToString(),
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            TieredCompilation = AppContext.GetData("System.Runtime.TieredCompilation")?.ToString(),
            Error = error,
            ExpectedScenes = Scenes.Select(scene => scene.Name),
            ElapsedSeconds = Stopwatch.GetElapsedTime(_testStarted).TotalSeconds,
            Timing = "Complete CPU critical path through Present; complete calibrated GPU application interval; pass command work; completed geometry counters",
            Instrumentation = "Four timestamp queries per executed pass; measured commands recorded each frame; same-queue resolves after application work",
            ProcessorCount = Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT"),
            TieredCompilationEnvironment = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            ReadyToRun = Environment.GetEnvironmentVariable("DOTNET_ReadyToRun"),
            Sampling = new { MinimumReadyFrames, MinimumRenderedFrames, WarmupSeconds, SampleSeconds, MaximumTestSeconds },
            TraceEnabled = _recordTrace || Options.TraceOutput is not null,
            Options.TraceDetail, Options.TraceAllocations,
            Options.TemporalAntialiasing, Options.VirtualShadows, Options.AsyncCompute, Options.ForceHardwareRaster,
            Options.UseInstanceGroups, Options.FixedFrameSeconds,
            Options.SynchronousFrames, Options.ScenePlayback, Options.DisableMeshShaders, Options.DeviceValidation,
            Options.HideWindow, Options.HideDebugUi, Options.SkipSwapchainPresent,
            Options.SurfacePanel,
            Options.WorldNameplates,
            Options.HudOverlay,
            Options.HudNumbersChange,
            Options.WindowVSync,
            RebuildDiagnosticsEnabled = SomeEngine.Views.RebuildDiagnostics.Enabled,
            HudWorkload = new { HudOverlay.StaticLabelCount, HudOverlay.ChangingNumberCount, HudOverlay.ListItemCount },
            GeometryMemoryMegabytes,
            Scenes = _results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Performance report: {LastReport}");
        IsMeasuring = false;
        ReloadRequested = !ExitAfterMeasurements;
        Options = _interactiveOptions!;
        SelectedScene = _interactiveScene;
        _interactiveOptions = null;
    }

    internal void Draw()
    {
        ImGui.SeparatorText("Test scenes");
        ImGui.TextUnformatted(string.Join("   ", Scenes.Take(3).Select((scene, index) => $"F{index + 2}: {scene.Name}")) + "   F6: HUD");
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
        bool surfacePanel = Options.SurfacePanel;
        bool worldNameplates = Options.WorldNameplates;
        bool hudOverlay = Options.HudOverlay;
        bool hudNumbersChange = Options.HudNumbersChange;
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
        changed |= ImGui.Checkbox("Surface panel", ref surfacePanel);
        changed |= ImGui.Checkbox("World nameplates", ref worldNameplates);
        changed |= ImGui.Checkbox("HUD overlay", ref hudOverlay);
        changed |= ImGui.Checkbox("Update HUD numbers", ref hudNumbersChange);
        changed |= ImGui.Combo("Playback", ref playback, playbacks, playbacks.Length);
        if (changed)
        {
            Options = Options with
            {
                TemporalAntialiasing = taa, VirtualShadows = shadows, AsyncCompute = asyncCompute,
                ForceHardwareRaster = hardwareRaster, UseInstanceGroups = groups, WindowVSync = vsync,
                DisableMeshShaders = !meshShaders, SynchronousFrames = synchronous, DeviceValidation = validation,
                SurfacePanel = surfacePanel,
                WorldNameplates = worldNameplates,
                HudOverlay = hudOverlay,
                HudNumbersChange = hudNumbersChange,
                ScenePlayback = playback switch { 1 => "motion", 2 => "materials", 3 => "camera", _ => null },
            };
            ReloadRequested = true;
        }
    }

    private object Summarize()
    {
        ApplicationFrameTiming[] frames = _frames.Select(frame => _completedFrames[frame.Frame]).ToArray();
        string[] stages = frames.SelectMany(frame => frame.Gpu.Passes).Select(pass => pass.Group ?? pass.Label).Distinct().Order().ToArray();
        return new
        {
            Frames = frames.Length,
            CpuMs = Distribution(frames.Select(frame => frame.CpuMs)),
            GameMs = Distribution(frames.Select(frame => frame.GameMs)),
            AnimationMs = Distribution(frames.Select(frame => frame.AnimationMs)),
            ExtractionMs = Distribution(frames.Select(frame => frame.ExtractionMs)),
            PrepareMs = Distribution(frames.Select(frame => frame.PrepareMs)),
            ReconcileMs = Distribution(frames.Select(frame => frame.ReconcileMs)),
            ExecuteMs = Distribution(frames.Select(frame => frame.ExecuteMs)),
            GpuMs = Distribution(frames.Select(frame => frame.Gpu.ElapsedMs)),
            GpuStagesMs = stages.ToDictionary(stage => stage, stage => Distribution(frames.Select(frame =>
                frame.Gpu.Passes.Where(pass => (pass.Group ?? pass.Label) == stage).Sum(pass => pass.CommandsMs)))),
            GpuPasses = Distribution(frames.Select(frame => (double)frame.Gpu.Passes.Count)),
            TimestampQueries = Distribution(frames.Select(frame => frame.Gpu.Passes.Count * 4.0)),
            Queues = frames.SelectMany(frame => frame.Gpu.Passes).Select(pass => $"{pass.QueueType}:{pass.QueueIndex}").Distinct().ToArray(),
            SelectedVertexItems = Distribution(frames.Select(frame => (double)frame.Geometry.SelectedVertices)),
            SelectedTriangleItems = Distribution(frames.Select(frame => (double)frame.Geometry.SelectedTriangles)),
            PhaseOneSoftwareClusters = Distribution(frames.Select(frame => (double)frame.Geometry.PhaseOneSoftwareClusters)),
            PhaseOneHardwareClusters = Distribution(frames.Select(frame => (double)frame.Geometry.PhaseOneHardwareClusters)),
            PhaseTwoSoftwareClusters = Distribution(frames.Select(frame => (double)frame.Geometry.PhaseTwoSoftwareClusters)),
            PhaseTwoHardwareClusters = Distribution(frames.Select(frame => (double)frame.Geometry.PhaseTwoHardwareClusters)),
            CandidateClusters = Distribution(frames.Select(frame => (double)frame.Geometry.CandidateCount)),
            ShadingWorkItems = Distribution(frames.Select(frame => (double)frame.Geometry.ShadingWorkItems)),
            DeformCacheReservedBytes = Distribution(frames.Select(frame => (double)frame.Geometry.DeformCacheBytes)),
        };
    }

    private static TimingDistribution Distribution(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        return new(ordered[0], ordered.Average(), (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2,
            ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], ordered[^1]);
    }

    private readonly record struct TimingDistribution(double Min, double Mean, double Median, double P95, double Max);
    internal readonly record struct FrameTiming(int Frame, double ElapsedMs, double PrepareMs, double ReconcileMs, double ExecuteMs,
        ViewTiming Views, double ExtractionMs, long ExtractionAllocatedBytes);
    private sealed record SceneTiming(string Scene, string Status, int Frames, double? MeanMs, double? MedianMs,
        double? P95Ms, double? PrepareMs, double? ReconcileMs, double? ExecuteMs,
        uint ResidentPages, uint RegisteredPages, int QueuedLoads, int ActiveLoads,
        uint GeometryUsedBytes, uint GeometryFreeBytes, ulong BackpressuredPages,
        int RenderWidth, int RenderHeight, bool AnimateScene,
        double FirstFrameMs, int StreamingFrames, double StreamingMs, double StreamingMaxFrameMs, ulong DroppedPageFaults,
        long Rebuilds, double? RebuildsPerFrame, long Computations, double? ComputationsPerFrame,
        double? RebuildMs, double? TextMs, double? LayoutMs, double? ViewMs, double? ExtractionMs,
        double? ViewAllocatedBytes, double? ExtractionAllocatedBytes, double? AnimationMs)
    {
        public object? Costs { get; init; }
        public object? Workload { get; init; }
    }
}

