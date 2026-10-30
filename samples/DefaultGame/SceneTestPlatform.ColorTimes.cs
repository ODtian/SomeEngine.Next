using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Runtime;

namespace DefaultGame;

public sealed partial class SceneTestPlatform
{
    private SceneColorTimes? _colorTimes;

    internal void InstallColorTimes(string[] args)
    {
        string? option = args.FirstOrDefault(static value => value.StartsWith("--color-times=", StringComparison.Ordinal));
        if (option is null) return;
        string mode = option["--color-times=".Length..];
        if (mode is not ("control" or "coarse"))
            throw new ArgumentException("--color-times requires 'control' or 'coarse'.", nameof(args));
        _colorTimes = new SceneColorTimes(this, mode);
        Console.WriteLine($"Color time mode: {mode}.");
        Application.InstallGeometry(_colorTimes);
        Application.UpdateFrame += _colorTimes.UpdateFrame;
        Application.FrameCompleted += _colorTimes.FrameCompleted;
    }

    private void AddColorTimesReport(JsonObject report)
    {
        if (_colorTimes is null) return;
        report["ColorTimes"] = JsonSerializer.SerializeToNode(_colorTimes.Reports,
            SceneJsonContext.Default.ListSceneColorTimeReport);
    }
}

internal sealed class SceneColorTimes(SceneTestPlatform platform, string mode) : IApplicationGeometryFeature
{
    private ColorTimeResources? _resources;
    internal List<SceneColorTimeReport> Reports { get; } = [];

    public IDisposable Install(ApplicationGeometryContext context)
    {
        var resources = new ColorTimeResources(context, platform, mode);
        _resources = resources;
        context.AddFrameSystem(resources);
        context.AddCompletionObserver(resources);
        return resources;
    }

    internal void UpdateFrame(ApplicationFrame frame) => _resources?.UpdateFrame(frame);

    internal void FrameCompleted(ApplicationFrame frame) => _resources?.FrameCompleted(frame);

    internal string? FinishScene(ApplicationFrameTimes[] presented)
    {
        ColorTimeResources resources = _resources!;
        SceneColorTimeFrame[] frames = resources.Frames.OrderBy(static frame => frame.SimulationFrame).ToArray();
        string? error = resources.Order is null ? "The color-pass order measurement did not finish." : null;
        if (mode == "coarse" && !presented.Select(static frame => frame.SimulationFrame)
                .SequenceEqual(frames.Select(static frame => frame.SimulationFrame)))
            error ??= "Color GPU timestamps did not cover the presented simulation-frame window.";
        if (frames.Select(static frame => frame.GraphFrameIdentity).Distinct().Count() != frames.Length)
            error ??= "Color GPU timestamps contain a repeated graph-frame identity.";
        Reports.Add(new(resources.Configuration!, mode, error is null, error, resources.Order,
            resources.Frequency, presented.Length, frames));
        return error;
    }

    private sealed class ColorTimeResources : ISystem<RenderFrameSystemContext>, IRenderGraphFrameExecutionObserver, IDisposable
    {
        private readonly IGraphicsBackend _backend;
        private readonly Device _device;
        private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
        private readonly SceneTestPlatform _platform;
        private readonly string _mode;
        private readonly SimulationFrameWindow _window;
        private readonly List<ColorTimeQueries> _queryResources = [];
        private ColorTimeQueries? _queries;
        private GraphPassId[] _colors = [];
        private GraphPassId[]? _ends;
        private SomeEngine.Graphics.Queue? _queue;
        private int _simulationFrame;
        private ulong _graphFrame;
        private bool _orderStarted;
        private bool _orderInstalled;
        private int _observedFrame;
        private ulong _observedGraphFrame;
        private ulong _observedBegin;
        private ulong _observedEnd;

        internal ColorTimeResources(ApplicationGeometryContext context, SceneTestPlatform platform, string mode)
        {
            _backend = context.Backend;
            _device = context.Device;
            _graph = context.Graph;
            _platform = platform;
            _mode = mode;
            _window = platform.Current.MeasurementFrames!;
            Frequency = _backend.CalibrateTimestamps(_backend.GetQueue(_device, QueueType.Graphics)).QueueFrequency;
        }

        internal ulong Frequency { get; }
        internal SceneColorTimeConfiguration? Configuration { get; private set; }
        internal SceneColorTimeOrder? Order { get; private set; }
        internal List<SceneColorTimeFrame> Frames { get; } = [];

        internal void UpdateFrame(ApplicationFrame frame)
        {
            _simulationFrame = frame.Index;
            if (frame.Index == 0)
            {
                RuntimeStartupOptions options = _platform.Application.Options;
                SampleSceneOptions sample = _platform.SampleOptions;
                Configuration = new(_platform.Current.Name, _mode, !RuntimeFeature.IsDynamicCodeSupported,
                    frame.Width, frame.Height, options.GraphicsBackend.ToString(), frame.Adapter.Name,
                    frame.Adapter.DriverVersion, frame.Adapter.Id.Low, frame.Adapter.Id.High, _window,
                    options.ShadingPath.ToString(), _graph.Description.MaximumFramesInFlight,
                    options.FixedFrameSeconds, sample.ScenePlayback, sample.DynamicScene,
                    sample.SurfacePanel, sample.WorldNameplates, sample.HudOverlay,
                    _platform.Application.DebugUiEnabled && !options.HideDebugUi, options.DeviceValidation,
                    options.SynchronousFrames, options.AsyncCompute, options.TemporalAntialiasing, options.VirtualShadows);
                Console.WriteLine("COLOR_CONFIGURATION " + JsonSerializer.Serialize(Configuration,
                    SceneJsonContext.Default.SceneColorTimeConfiguration));
            }
            if (frame.Index == _window.FirstFrame / 2 && !_orderStarted)
                MeasureOrder();
            else if (_ends is { } ends && !_orderInstalled)
                InstallOrder(ends);
        }

        private void MeasureOrder()
        {
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                var colors = new List<GraphPassId>();
                foreach (GraphPassId pass in edit.GetPasses(default))
                    if (edit.GetPassLabel(pass).StartsWith("Evaluate Cluster color bin ", StringComparison.Ordinal))
                        colors.Add(pass);
                _colors = colors.ToArray();
                _queries = new(this, ref edit, _colors, "order");
                _queryResources.Add(_queries);
                edit.Commit();
                _orderStarted = true;
            }
            finally { edit.Dispose(); }
        }

        private void InstallOrder(GraphPassId[] ends)
        {
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                ColorTimeQueries previous = _queries!;
                previous.Remove(ref edit);
                foreach (GraphPassId pass in _colors)
                {
                    if (pass != ends[0]) edit.OrderAfter(pass, ends[0]);
                    if (pass != ends[^1]) edit.OrderAfter(ends[^1], pass);
                }
                Order = new(_observedFrame, _observedGraphFrame, _observedBegin, _observedEnd,
                    _simulationFrame, _colors.Length, edit.GetPassLabel(ends[0]),
                    edit.GetPassLabel(ends[^1]), _queue!.Type.ToString(), _queue.Index, ends.Length);
                _queries = _mode == "coarse" ? new(this, ref edit, ends, "span") : null;
                if (_queries is { } queries) _queryResources.Add(queries);
                edit.Commit();
                previous.Retire();
                _orderInstalled = true;
                Console.WriteLine("COLOR_ORDER " + JsonSerializer.Serialize(Order, SceneJsonContext.Default.SceneColorTimeOrder));
            }
            finally { edit.Dispose(); }
        }

        public void OnUpdate(ref RenderFrameSystemContext context)
        {
            _graphFrame = context.Graph.FrameIdentity;
            _queries?.Stage(context.Graph, _simulationFrame);
        }

        internal void FrameCompleted(ApplicationFrame frame)
        {
            if (frame.Timing.SimulationFrame % 256 != 0) return;
            var progress = new SceneColorTimeProgress(_platform.Current.Name, _mode,
                frame.Timing.SimulationFrame, _graphFrame, frame.Width, frame.Height,
                _orderInstalled ? _mode : _orderStarted ? "order" : "warmup", _colors.Length);
            Console.WriteLine("COLOR_PROGRESS " + JsonSerializer.Serialize(progress,
                SceneJsonContext.Default.SceneColorTimeProgress));
        }

        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            if (_queries is not { } queries) return;
            if (!result.TryGetReadbackCompletion(queries.Readback, out _))
            {
                queries.Discard(_graphFrame);
                return;
            }
            if (_queue is not null) return;
            foreach (GraphPassId pass in _colors)
            {
                if (!result.TryGetPassCompletion(pass, out QueueCompletion completion))
                    throw new InvalidOperationException("A color pass was absent from its order measurement.");
                if (_queue is null) _queue = completion.Queue;
                else if (!ReferenceEquals(_queue, completion.Queue))
                    throw new InvalidOperationException("Color passes used different queues.");
            }
        }

        private void RecordTimes(ColorTimeQueries source, ReadOnlySpan<ulong> ticks, int frame, ulong graphFrame)
        {
            if (source.Kind == "order")
            {
                if (_ends is not null) return;
                int first = 0, last = 0;
                for (int index = 0; index < source.Passes.Length; index++)
                {
                    if (ticks[index * 2] < ticks[first * 2]) first = index;
                    if (ticks[index * 2 + 1] > ticks[last * 2 + 1]) last = index;
                }
                _ends = first == last ? [source.Passes[first]] : [source.Passes[first], source.Passes[last]];
                _observedFrame = frame;
                _observedGraphFrame = graphFrame;
                _observedBegin = ticks[first * 2];
                _observedEnd = ticks[last * 2 + 1];
                return;
            }
            if (frame < _window.FirstFrame || frame >= _window.FirstFrame + _window.FrameCount) return;
            int lastPair = ticks.Length - 2;
            ulong firstBegin = ticks[0], firstEnd = ticks[1];
            ulong lastBegin = ticks[lastPair], lastEnd = ticks[lastPair + 1];
            if (firstBegin == 0 || firstBegin > firstEnd || lastBegin > lastEnd ||
                firstBegin > lastBegin || firstEnd > lastEnd)
                throw new InvalidDataException("Color boundary timestamps must contain ordered pass intervals.");
            Frames.Add(new(frame, graphFrame, firstBegin, firstEnd, lastBegin, lastEnd));
        }

        public void Dispose()
        {
            foreach (ColorTimeQueries queries in _queryResources) queries.Dispose();
        }

        private sealed class ColorTimeQueries : IGraphReadbackReceiver, IDisposable
        {
            private readonly ColorTimeResources _resources;
            private readonly QueryPool _pool;
            private readonly GraphQueryPoolId _queryId;
            private readonly GraphBufferId _values;
            private readonly GraphPassId _resolve;
            private readonly Dictionary<ulong, int> _frames = [];
            private bool _removed;
            internal GraphReadbackId Readback { get; }
            internal GraphPassId[] Passes { get; }
            internal string Kind { get; }

            internal ColorTimeQueries(ColorTimeResources resources, ref RenderGraphEdit edit, GraphPassId[] passes, string kind)
            {
                _resources = resources;
                Passes = passes;
                Kind = kind;
                uint count = (uint)passes.Length * 2;
                _pool = resources._backend.CreateQueryPool(resources._device,
                    new(QueryType.Timestamp, QueueType.Graphics, count, Label: "Scene color times"));
                _queryId = edit.RegisterQueryPool(_pool);
                for (int index = 0; index < passes.Length; index++)
                    edit.MeasurePassTime(passes[index], _queryId, (uint)index * 2);
                _values = edit.CreateBuffer(new BufferDesc(count * sizeof(ulong),
                    BufferUsages.QueryResolve | BufferUsages.CopyDestination | BufferUsages.CopySource,
                    "Scene color timestamp values"));
                _resolve = edit.AddGeneralFramePass("Resolve scene color times", PassQueueSelection.AnyOfType(QueueType.Graphics),
                    new ColorQueryResults(_queryId, _values, count),
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    static (ref PassDefinition pass, ref ColorQueryResults data) =>
                    {
                        pass.Read(data.Pool, new QueryRange(0, data.Count));
                        pass.Write(data.Values, BufferRange.Whole, PipelineSync.Copy,
                            ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    },
                    static (ref GeneralPassCommandScope commands, in ColorQueryResults data) =>
                        commands.ResolveQueries(data.Pool, 0, data.Count, commands.GetBuffer(data.Values),
                            new BufferRange(0, data.Count * sizeof(ulong))));
                Readback = edit.AddReadback(_values, BufferRange.Whole, this, "Read scene color times");
                edit.OrderAfter(Readback, _resolve);
            }

            internal void Stage(RenderGraphFrame graph, int frame)
            {
                _frames.Add(graph.FrameIdentity, frame);
                graph.SetReadbackTag(Readback, graph.FrameIdentity);
            }

            internal void Discard(ulong frame) => _frames.Remove(frame);

            public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
            {
                int frame = _frames[tag];
                _frames.Remove(tag);
                _resources.RecordTimes(this, MemoryMarshal.Cast<byte, ulong>(bytes), frame, tag);
            }

            internal void Remove(ref RenderGraphEdit edit)
            {
                edit.Remove(Readback);
                edit.Remove(_resolve);
                edit.Remove(_values);
                edit.Remove(_queryId);
            }

            internal void Retire()
            {
                _resources._graph.RetireAfterSubmittedFrames(_pool);
                _removed = true;
            }

            public void Dispose()
            {
                if (!_removed) _pool.Dispose();
            }

            private readonly record struct ColorQueryResults(GraphQueryPoolId Pool, GraphBufferId Values, uint Count);
        }
    }
}

internal sealed record SceneColorTimeConfiguration(string Scene, string Mode, bool NativeAot,
    int Width, int Height, string Backend, string Device, string Driver, ulong AdapterIdLow, ulong AdapterIdHigh,
    SimulationFrameWindow MeasurementFrames, string ShadingPath, uint MaximumFramesInFlight,
    float FixedFrameSeconds, string? ScenePlayback, bool Animate,
    bool SurfacePanel, bool WorldNameplates, bool HudOverlay, bool DebugUi, bool DeviceValidation,
    bool SynchronousFrames, bool AsyncCompute, bool TemporalAntialiasing, bool VirtualShadows);

internal sealed record SceneColorTimeProgress(string Scene, string Mode, int SimulationFrame,
    ulong GraphFrameIdentity, int Width, int Height, string Phase, int ColorPassCount);

internal sealed record SceneColorTimeOrder(int ObservedSimulationFrame, ulong ObservedGraphFrameIdentity,
    ulong ObservedFirstBegin, ulong ObservedLastEnd, int InstalledSimulationFrame, int ColorPassCount,
    string FirstPass, string LastPass, string QueueType, uint QueueIndex, int MeasuredPassCount)
{
    public string Coverage => "Color passes execute between the observed first and last pass on the same queue; explicit pass order maintains both boundaries.";
    public int InternalTimestampBoundaries => MeasuredPassCount == 2 ? 2 : 0;
}

internal readonly record struct SceneColorTimeFrame(int SimulationFrame, ulong GraphFrameIdentity,
    ulong FirstBegin, ulong FirstEnd, ulong LastBegin, ulong LastEnd);

internal sealed record SceneColorTimeReport(SceneColorTimeConfiguration Configuration, string Mode,
    bool Complete, string? Error, SceneColorTimeOrder? Order, ulong TimestampFrequency,
    int PresentedFrameCount, SceneColorTimeFrame[] Frames)
{
    public bool DiagnosticOnly => true;
    public string Scope => "First color commands begin through last color commands end; multiply tick differences by 1000/TimestampFrequency for milliseconds. Each measured pass adds command-begin and command-end timestamps. Distinct first/last passes add two internal timing boundaries. Other color passes retain their disjoint-write declarations.";
}
