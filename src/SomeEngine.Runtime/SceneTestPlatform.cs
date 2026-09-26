using ImGuiNET;
using System.Diagnostics;
using System.Text.Json;
using SomeEngine.Diagnostics;
using SomeEngine.Render.Cluster;

namespace SomeEngine.Runtime;

public sealed record TestScene(string Name, string ContentDirectory, bool Animate = false);

/// <summary>Shared scene selection and render settings for the interactive test application.</summary>
public sealed class SceneTestPlatform
{
    public SceneTestPlatform(IReadOnlyList<TestScene> scenes)
    {
        if (scenes.Count == 0) throw new ArgumentException("At least one test scene is required.", nameof(scenes));
        Scenes = scenes.ToArray();
        Options = RuntimeStartupOptions.Parse([]) with
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
    private long _testStarted;
    private long _sceneStarted;
    private long _sampleStarted;
    private long _renderingStarted;
    private int _renderedFrames;
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
    public string? LastReport { get; private set; }
    public int GeometryMemoryMegabytes { get; private set; } = 256;
    internal bool ImageRequested { get; private set; }
    internal string? LastImage { get; private set; }

    internal void SetRenderingInfo(int width, int height, string device, string driver)
        => (_renderWidth, _renderHeight, _graphicsDevice, _graphicsDriver) = (width, height, device, driver);

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

    public void StartMeasurements(bool recordTrace = false)
    {
        if (IsMeasuring) return;
        _results.Clear();
        _recordTrace = recordTrace;
        _interactiveOptions = Options;
        _interactiveScene = SelectedScene;
        Options = Options with { WindowVSync = false, FixedFrameSeconds = 1f / 60f };
        _resultDirectory = Path.Combine(AppContext.BaseDirectory, "Performance", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
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
        _sceneTimeLimit = (45 - Stopwatch.GetElapsedTime(_testStarted).TotalSeconds) / (Scenes.Count - SelectedScene);
        _sceneStarted = Stopwatch.GetTimestamp();
        _sampleStarted = 0;
        _renderingStarted = 0;
        _renderedFrames = 0;
        _readyFrames = 0;
        _frames.Clear();
        if (_recordTrace)
            _trace = EngineTrace.Start(Path.Combine(_resultDirectory!, $"scene-{SelectedScene}.setrace"),
                new SomeEngine.Diagnostics.TraceOptions
                {
                    Detail = Options.TraceDetail, Allocations = Options.TraceAllocations,
                    Metadata = new Dictionary<string, string> { ["scene"] = Current.Name },
                });
    }

    internal void RecordFrame(FrameTiming frame, ClusterRenderDiagnostics diagnostics)
    {
        if (!IsMeasuring) return;
        if (_renderingStarted == 0) _renderingStarted = Stopwatch.GetTimestamp();
        _renderedFrames++;
        var residency = diagnostics.Residency;
        bool ready = diagnostics.Meshes.PublishedMeshes == diagnostics.Meshes.RegisteredMeshes &&
            residency.QueuedPageLoads == 0 && residency.ActivePageLoads == 0 && residency.PageLoadFailures == 0;
        _readyFrames = ready ? _readyFrames + 1 : 0;
        double elapsed = Stopwatch.GetElapsedTime(_sceneStarted).TotalSeconds;
        if (_readyFrames >= 32 && _renderedFrames >= 128 && Stopwatch.GetElapsedTime(_renderingStarted).TotalSeconds >= 3)
        {
            if (_sampleStarted == 0) _sampleStarted = Stopwatch.GetTimestamp();
            _frames.Add(frame);
        }
        else
        {
            _sampleStarted = 0;
            _frames.Clear();
        }
        bool sampled = _sampleStarted != 0 && Stopwatch.GetElapsedTime(_sampleStarted).TotalSeconds >= 2;
        bool expired = elapsed >= _sceneTimeLimit || Stopwatch.GetElapsedTime(_testStarted).TotalSeconds >= 45;
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
            _renderWidth, _renderHeight, Options.DynamicScene));
        ReloadRequested = true;
    }

    internal void EndScene(string? error = null)
    {
        _trace?.Dispose();
        _trace = null;
        if (!IsMeasuring || _sceneStarted == 0) return;
        if (error is null && ReloadRequested && SelectedScene + 1 < Scenes.Count && Stopwatch.GetElapsedTime(_testStarted).TotalSeconds < 45)
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
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            TieredCompilation = AppContext.GetData("System.Runtime.TieredCompilation")?.ToString(),
            Error = error,
            ExpectedScenes = Scenes.Select(scene => scene.Name),
            ElapsedSeconds = Stopwatch.GetElapsedTime(_testStarted).TotalSeconds,
            Timing = "Presented frame elapsed time and CPU-side stage elapsed time; GPU duration is not measured",
            TraceEnabled = _recordTrace,
            Options.TraceDetail, Options.TraceAllocations,
            Options.TemporalAntialiasing, Options.VirtualShadows, Options.AsyncCompute, Options.ForceHardwareRaster,
            Options.UseInstanceGroups, Options.FixedFrameSeconds,
            Options.SynchronousFrames, Options.ScenePlayback, Options.DisableMeshShaders, Options.DeviceValidation,
            GeometryMemoryMegabytes,
            Scenes = _results,
        }, new JsonSerializerOptions { WriteIndented = true }));
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

    internal readonly record struct FrameTiming(double ElapsedMs, double PrepareMs, double ReconcileMs, double ExecuteMs);
    private sealed record SceneTiming(string Scene, string Status, int Frames, double? MeanMs, double? MedianMs,
        double? P95Ms, double? PrepareMs, double? ReconcileMs, double? ExecuteMs,
        uint ResidentPages, uint RegisteredPages, int QueuedLoads, int ActiveLoads,
        uint GeometryUsedBytes, uint GeometryFreeBytes, ulong BackpressuredPages,
        int RenderWidth, int RenderHeight, bool AnimateScene);
}

