using System.Diagnostics;
using System.Text.Json;
using ImGuiNET;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Systems;
using SomeEngine.Runtime;

namespace SomeEngine.Liquid;

public sealed class LiquidDemo
{
    private World _game = null!;
    private RenderWorld _render = null!;
    private Entity _liquid;
    private LiquidExtraction _extraction = null!;
    private int _revision;
    private bool _paused, _loop;
    private bool _closeUp;
    private double _accumulated;
    private long _previous;
    private float _stepMs;
    private readonly bool _orbit;
    private readonly int _display;
    private readonly int _pointLimit;
    private string _scene = "pour";
    private LiquidFluid _fluid;
    private LiquidEmission? _emission;
    private float _extent = 1;
    private readonly string? _captureDirectory;
    private readonly int _captureFrame;
    private readonly Process? _video;
    private LiquidRendering _renderer = null!;
    private readonly List<(double Cpu, double Extraction, double Frame, double[] Gpu, int Count)> _measurements = [];
    private string? _measurePath;
    private ApplicationScene _application = null!;

    private LiquidDemo(bool orbit, int display, int pointLimit, string? captureDirectory, int captureFrame, Process? video) =>
        (_orbit, _display, _pointLimit, _captureDirectory, _captureFrame, _video) =
        (orbit, display, pointLimit, captureDirectory, captureFrame, video);

    public static int ReadPointLimit(string[] arguments) => int.Parse(arguments.FirstOrDefault(
        argument => argument.StartsWith("--control-points=", StringComparison.Ordinal))?["--control-points=".Length..] ?? "64");

    public static async Task<int> Main(string[] args)
    {
        int dynamicsArgument = Array.IndexOf(args, "--dynamics");
        if (dynamicsArgument >= 0)
        {
            string scene = args.FirstOrDefault(argument => argument.StartsWith("--scene="))?[8..] ?? "curved";
            LiquidFluid fluid = Enum.Parse<LiquidFluid>(args.FirstOrDefault(argument => argument.StartsWith("--fluid="))?[8..] ?? "water", true);
            LiquidMeasurements.WriteDynamics(args[dynamicsArgument + 1], new LiquidFlow(ReadPointLimit(args), scene, fluid: fluid));
            return 0;
        }
        int settlingArgument = Array.IndexOf(args, "--settling");
        if (settlingArgument >= 0)
        {
            LiquidMeasurements.WriteSettling(args[settlingArgument + 1], ReadPointLimit(args));
            return 0;
        }
        string content = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../DefaultGame"));
        int contentArgument = Array.IndexOf(args, "--content");
        if (contentArgument >= 0) content = Path.GetFullPath(args[contentArgument + 1]);
        var options = RuntimeStartupOptions.Parse(args) with
        {
            DynamicScene = false, SynchronousFrames = true, TemporalAntialiasing = false, VirtualShadows = false,
            RequiredDeviceFeatures = SomeEngine.Graphics.DeviceFeatures.CalibratedTimestamps,
        };
        int captureArgument = Array.IndexOf(args, "--capture");
        int captureFrame = (int)(60 * float.Parse(args.FirstOrDefault(argument => argument.StartsWith("--time="))?[7..] ?? "1.8",
            System.Globalization.CultureInfo.InvariantCulture));
        string? captureDirectory = captureArgument >= 0 ? Path.GetFullPath(args[captureArgument + 1]) : null;
        if (captureDirectory is not null)
        {
            Directory.CreateDirectory(captureDirectory);
            options = options with { FrameLimit = captureFrame + 5, FixedFrameSeconds = 1f / 60, HideWindow = true, HideDebugUi = true, WindowVSync = false };
        }
        int videoArgument = Array.IndexOf(args, "--record");
        using Process? video = videoArgument >= 0 ? StartVideo(Path.GetFullPath(args[videoArgument + 1])) : null;
        if (video is not null)
            options = options with { FrameLimit = 240, FixedFrameSeconds = 1f / 30, HideWindow = true, HideDebugUi = true, WindowVSync = false };
        int display = args.Contains("--footprints") ? 5 : args.Contains("--wireframe") ? 3 : args.Contains("--thickness") ? 2 : args.Contains("--normals") ? 4 : args.Contains("--points") ? 1 : 0;
        var demo = new LiquidDemo(args.Contains("--orbit"), display, ReadPointLimit(args), captureDirectory, captureFrame, video);
        demo._closeUp = args.Contains("--close-up");
        demo._scene = args.FirstOrDefault(argument => argument.StartsWith("--scene="))?[8..] ?? "pour";
        demo._fluid = Enum.Parse<LiquidFluid>(args.FirstOrDefault(argument => argument.StartsWith("--fluid="))?[8..] ?? "water", true);
        string? emission = args.FirstOrDefault(argument => argument.StartsWith("--emission="))?[11..];
        if (emission is not null) demo._emission = Enum.Parse<LiquidEmission>(emission, true);
        demo._extent = float.Parse(args.FirstOrDefault(argument => argument.StartsWith("--extent="))?[9..] ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        int measureArgument = Array.IndexOf(args, "--measure");
        if (measureArgument >= 0)
        {
            demo._measurePath = Path.GetFullPath(args[measureArgument + 1]);
            options = options with { FrameLimit = 360, FixedFrameSeconds = 1f / 60, HideWindow = true, HideDebugUi = true, WindowVSync = false };
        }
        var application = new ApplicationScene(content)
        {
            WindowTitle = $"SomeEngine / {demo._fluid} / liquid sections and curved contacts",
            Options = options,
            InitializeWorlds = demo.Initialize,
            UpdateFrame = frame => { demo.ClearSample(frame); demo.Update(frame); },
            DrawDebugUi = demo.DrawControls,
            CreateColorFeature = rendering => demo._renderer = new LiquidRendering(rendering, demo._render, demo._extraction),
            FrameCompleted = demo.Measure,
        };
        demo._application = application;
        int result = await Application.RunAsync(args, application: application);
        if (result == 0 && demo._measurePath is not null) demo.SaveMeasurements();
        if (video is not null)
        {
            video.StandardInput.Close();
            video.WaitForExit();
            if (result == 0) result = video.ExitCode;
        }
        return result;
    }

    public static void Install(ApplicationScene application, int pointLimit = 64)
    {
        application.Options = application.Options with
        {
            SynchronousFrames = true, TemporalAntialiasing = false, VirtualShadows = false,
            RequiredDeviceFeatures = application.Options.RequiredDeviceFeatures | SomeEngine.Graphics.DeviceFeatures.CalibratedTimestamps,
        };
        var demo = new LiquidDemo(false, 0, pointLimit, null, 0, null) { _application = application };
        application.InitializeWorlds += demo.Initialize;
        application.UpdateFrame += demo.Update;
        application.DrawDebugUi += demo.DrawControls;
        application.CreateColorFeature = rendering => demo._renderer = new LiquidRendering(rendering, demo._render, demo._extraction);
    }

    private void Measure(ApplicationFrame frame)
    {
        if (_measurePath is not null && frame.Index >= 120)
            _measurements.Add((_stepMs, _extraction.CopyMilliseconds, frame.Timing.ElapsedMs, _renderer.ReadTimes(), _game.Read<LiquidBody>(_liquid).Flow.Count));
    }

    private void SaveMeasurements()
    {
        var report = new
        {
            Width = 1280, Height = 720, ControlPoints = _pointLimit, Surface = "Continuous sections",
            Backend = _renderer.BackendName,
            Scene = _scene, Fluid = _fluid.ToString(), Emission = _emission.ToString(),
            Extent = _extent, MeshVertices = LiquidRendering.MeshVertexCount, MeshSamples = LiquidRendering.MeshSamples, Frames = _measurements.Count,
            MinimumPoints = _measurements.Min(item => item.Count), MaximumPoints = _measurements.Max(item => item.Count), WarmupFrames = 120,
            FixedStep = 1f / 60, Trace = false,
            CpuMeanMs = _measurements.Average(item => item.Cpu),
            CpuExtractionMeanMs = _measurements.Average(item => item.Extraction),
            FrameMedianMs = _measurements.Select(item => item.Frame).Order().ElementAt(_measurements.Count / 2),
            GpuMeanMs = _measurements.Average(item => item.Gpu.Sum()),
            GpuTiming = "Dispatch and draw timestamp sum; presented frame includes multisample resolve and resource transitions.",
            GpuStagesMs = Enumerable.Range(0, _measurements[0].Gpu.Length)
                .Select(stage => _measurements.Average(item => item.Gpu[stage])).ToArray(),
            GpuStages = _renderer.StageNames,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_measurePath!)!);
        File.WriteAllText(_measurePath!, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(_measurePath!));
    }

    private static Process StartVideo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
        };
        foreach (string argument in new[] { "-y", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", "1280x720", "-framerate", "30", "-i", "pipe:0", "-an", "-c:v", "libx264",
            "-preset", "fast", "-crf", "12", "-pix_fmt", "yuv420p", "-movflags", "+faststart", path })
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private void Initialize(World game, RenderWorld render, RenderExtractionSystems extraction)
    {
        (_game, _render) = (game, render);
        var flow = new LiquidFlow(_pointLimit, _scene, _extent, _fluid, _emission);
        _emission = flow.Emission;
        var view = new LiquidView { Display = _display };
        if (_closeUp) { view.Target = _scene == "curved" ? new(-.28f, .9f, -.08f) : new(-.55f, 1.3f, 0); view.Distance = .65f; view.Pitch = 1.15f; }
        _liquid = game.CreateEntity(new LiquidBody(flow, view, 0));
        _extraction = new LiquidExtraction();
        extraction.Add(_extraction);
        _previous = Stopwatch.GetTimestamp();
    }

    private void ClearSample(ApplicationFrame frame)
    {
        if (frame.Index == 0)
        {
            foreach (Entity mesh in frame.Scene.MeshEntities.ToArray()) frame.Scene.DeleteEntity(mesh);
            frame.Scene.SetOutputSize(1280, 720);
        }
    }

    private void Update(ApplicationFrame frame)
    {
        LiquidBody body = _game.Read<LiquidBody>(_liquid);
        LiquidFlow flow = body.Flow;
        LiquidView view = body.View;
        if (frame.WasKeyPressed(0x20)) _paused = !_paused;
        if (frame.WasKeyPressed(0x52)) flow.Reset();
        if (frame.WasKeyPressed(0x31)) view.Display = 0;
        if (frame.WasKeyPressed(0x32)) view.Display = 1;
        if (frame.WasKeyPressed(0x33)) view.Display = 3;
        if (frame.WasKeyPressed(0x34)) view.Display = 2;
        if (frame.WasKeyPressed(0x35)) view.Display = 5;
        if (frame.WasKeyPressed(0x53)) flow.Stirring = !flow.Stirring;
        if (frame.WasKeyPressed(0x50)) flow.Pouring = !flow.Pouring;
        if (_captureDirectory is not null && frame.Index >= _captureFrame)
        {
            _paused = true;
            if (frame.Index == _captureFrame)
            {
                var state = new
                {
                    flow.Scene, Fluid = flow.Fluid.ToString(), Emission = flow.Emission.ToString(), flow.PointLimit, flow.Count, flow.Seconds, flow.Volume, flow.Extent,
                    Points = flow.Points.ToArray(),
                };
                File.WriteAllText(Path.Combine(_captureDirectory, "state.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions { IncludeFields = true }));
            }
            view.Display = (frame.Index - _captureFrame) switch { 0 => 0, 1 => 1, 2 => 3, 3 => 2, _ => 5 };
            string name = view.Display switch { 0 => "surface", 1 => "points", 3 => "wireframe", 5 => "footprints", _ => "thickness" };
            string path = Path.Combine(_captureDirectory, name + ".bmp");
            frame.Scene.CaptureImage(image => { File.Delete(path); image.SaveBmp(path); });
        }
        if (_video is not null)
            frame.Scene.CaptureImage(image => _video.StandardInput.BaseStream.Write(image.Pixels.Span));
        long now = Stopwatch.GetTimestamp();
        float fixedSeconds = _application.Options.FixedFrameSeconds;
        double elapsed = fixedSeconds > 0 ? fixedSeconds : Math.Min(.05, Stopwatch.GetElapsedTime(_previous, now).TotalSeconds);
        _previous = now;
        long started = Stopwatch.GetTimestamp();
        if (!_paused)
        {
            _accumulated += elapsed;
            while (_accumulated >= LiquidFlow.StepSeconds)
            {
                flow.Step();
                _accumulated -= LiquidFlow.StepSeconds;
            }
            if (_loop && flow.Seconds > 14) flow.Reset();
        }
        if (_orbit) view.Yaw = .55f + (float)flow.Seconds * .1f;

        _stepMs = (float)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _game.Replace(_liquid, body with { Revision = ++_revision });
    }

    private void DrawControls()
    {
        LiquidBody body = _game.Read<LiquidBody>(_liquid);
        LiquidFlow flow = body.Flow;
        LiquidView view = body.View;
        ImGui.SeparatorText("LIQUID / SPARSE CONTROL POINTS");
        int fluid = (int)flow.Fluid, emission = (int)flow.Emission;
        if (ImGui.Combo("Liquid", ref fluid, "Water\0Urine\0Semen\0")) flow.SetFluid((LiquidFluid)fluid);
        if (ImGui.Combo("Source", ref emission, "Continuous / taper / sputter\0Pulsed jet\0Separate drops\0")) flow.SetEmission((LiquidEmission)emission);
        ImGui.Text($"{flow.Count} / {flow.PointLimit} control points    {flow.Seconds:F1} s");
        ImGui.Text($"Volume {flow.Volume * 1e6:F2} ml    CPU {_stepMs:F2} ms");
        ImGui.Text($"Liquid volumes: {LiquidRendering.MeshVertexCount:N0} mesh vertices");
        if (ImGui.Button(_paused ? "Resume [Space]" : "Pause [Space]")) _paused = !_paused;
        ImGui.SameLine();
        if (ImGui.Button("Reset [R]")) flow.Reset();
        ImGui.Checkbox("Loop demonstration", ref _loop);
        bool pouring = flow.Pouring, stirring = flow.Stirring;
        if (ImGui.Checkbox("Pour [P]", ref pouring)) flow.Pouring = pouring;
        ImGui.SameLine();
        if (ImGui.Checkbox("Move obstacle [S]", ref stirring)) flow.Stirring = stirring;
        int display = view.Display;
        ImGui.RadioButton("Surface [1]", ref display, 0); ImGui.SameLine();
        ImGui.RadioButton("Control points [2]", ref display, 1);
        ImGui.RadioButton("Mesh edges [3]", ref display, 3); ImGui.SameLine();
        ImGui.RadioButton("Liquid thickness [4]", ref display, 2); view.Display = display;
        ImGui.RadioButton("Contact footprints [5]", ref display, 5); view.Display = display;
        float viscosity = flow.Viscosity, nozzle = flow.NozzleX;
        if (ImGui.SliderFloat("Dynamic viscosity (Pa s)", ref viscosity, .001f, .2f)) flow.Viscosity = viscosity;
        if (ImGui.SliderFloat("Nozzle", ref nozzle, -1.5f, 1.4f)) flow.NozzleX = nozzle;
        ImGui.SliderAngle("Orbit", ref view.Yaw, -180, 180);
        ImGui.SliderFloat("Elevation", ref view.Pitch, .1f, 1.2f);
        ImGui.SliderFloat("Distance", ref view.Distance, .3f, 9);
    }
}
