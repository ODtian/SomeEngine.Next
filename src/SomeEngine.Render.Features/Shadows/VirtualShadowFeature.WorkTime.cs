using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private QueryPool? _workTimeQueries;
    private GraphPassId _workTimeEndPass;
    private GraphReadbackId _workTimeReadback;
    private ulong _workTimeFrequency;
    private float _workTimeFraction = 1f;
    private int _workTimeFramesSinceChange;
    private float _frameWorkTimeFraction;
    private int _frameWorkTimeFramesSinceChange;
    private bool _frameWorkTimeClearsHistory;
    private bool _ignoreFirstWorkTime = true;
    private readonly List<WorkTimeSample> _workTimeSamples = [];
    private readonly Dictionary<ulong, WorkTimeFrame> _workTimeFrames = [];

    private readonly record struct WorkTimeSample(float Milliseconds, float Fraction);
    private sealed class WorkTimeFrame(float fraction)
    {
        internal readonly float Fraction = fraction;
        internal bool Complete;
        internal float? Milliseconds;
    }

    private void InstallWorkTime(ref RenderGraphEdit edit, GraphRegionId prepare, GraphRegionId finish, GraphRegionId feedback)
    {
        if (_shadowMap.Settings.WorkTimeBudgetMilliseconds <= 0f) return;
        _workTimeFrequency = _backend.CalibrateTimestamps(_backend.GetQueue(_device, QueueType.Graphics)).QueueFrequency;
        _workTimeQueries = _backend.CreateQueryPool(_device, new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 2,
            Label: "Virtual shadow geometry time"));
        GraphQueryPoolId queries = edit.RegisterQueryPool(_workTimeQueries);
        GraphBufferId results = edit.CreateBuffer(new BufferDesc(2u * sizeof(ulong), BufferUsages.CopyDestination | BufferUsages.CopySource | BufferUsages.QueryResolve,
            "Virtual shadow geometry timestamps"));
        GraphPassId start = edit.AddGeneralFramePass("Start virtual shadow geometry timing", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new WorkTimestamp(queries, 0), new PassOptions(Culling: PassCullingMode.NeverCull),
            WorkTimestamp.Declare, WorkTimestamp.Record, region: prepare);
        edit.OrderAfter(start, _clearPass);
        GraphPassId end = edit.AddGeneralFramePass("End virtual shadow geometry timing", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new WorkTimestamp(queries, 1), new PassOptions(Culling: PassCullingMode.NeverCull),
            WorkTimestamp.Declare, WorkTimestamp.Record, region: finish);
        _workTimeEndPass = end;
        edit.OrderAfter(end, _rasterCompletePass);
        GraphPassId resolve = edit.AddGeneralFramePass("Resolve virtual shadow geometry timing", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new WorkTimestampResults(queries, results), new PassOptions(Culling: PassCullingMode.NeverCull),
            WorkTimestampResults.Declare, WorkTimestampResults.Record, region: feedback);
        edit.OrderAfter(resolve, end);
        edit.OrderAfter(_frameCompletePass, resolve);
        _workTimeReadback = edit.AddReadback(results, BufferRange.Whole, new WorkTimeReceiver(this),
            "Observe virtual shadow geometry time", feedback);
        edit.OrderAfter(_workTimeReadback, resolve);
    }

    private float StageWorkTime(ref RenderGraphFrame graph)
    {
        if (_workTimeQueries is null) return -1f;
        RefreshWorkTimeFraction();
        _workTimeFrames.Add(_frameIndex, new WorkTimeFrame(_frameWorkTimeFraction));
        graph.SetReadbackTag(_workTimeReadback, _frameIndex);
        graph.SetReadbackEnabled(_workTimeReadback, true);
        return Math.Clamp(1f - _frameWorkTimeFraction, 0f, 1f);
    }

    private void RefreshWorkTimeFraction()
    {
        _frameWorkTimeFraction = _workTimeFraction;
        _frameWorkTimeFramesSinceChange = _workTimeFramesSinceChange;
        _frameWorkTimeClearsHistory = false;
        if (_workTimeSamples.Count == 0) return;
        float budget = _shadowMap.Settings.WorkTimeBudgetMilliseconds;
        float target = budget * .95f;
        float sum = 0f, weightSum = 0f, weight = 1f;
        int consecutiveOverBudget = 0, count = 0;
        for (int index = _workTimeSamples.Count - 1; index >= 0; index--)
        {
            WorkTimeSample sample = _workTimeSamples[index];
            consecutiveOverBudget = sample.Milliseconds > budget ? consecutiveOverBudget + 1 : 0;
            sum += target / sample.Milliseconds * sample.Fraction * weight;
            weightSum += weight;
            count++;
            weight *= .9f;
            if (consecutiveOverBudget == 2) break;
        }
        float next = sum / weightSum;
        bool allOverBudget = count > 0 && consecutiveOverBudget == count;
        bool canChange = _workTimeFramesSinceChange >= 8;
        if (allOverBudget) _frameWorkTimeClearsHistory = true;
        else
        {
            bool enough = canChange && (next > _workTimeFraction * 1.01f || next < _workTimeFraction * .99f ||
                (next != _workTimeFraction && (next == 0f || next == 1f)));
            if (!enough) next = _workTimeFraction;
            if (next > _workTimeFraction) next = float.Lerp(_workTimeFraction, next, .9f);
        }
        next = Math.Clamp(next, 0f, 1f);
        if (next != _workTimeFraction && canChange)
        {
            _frameWorkTimeFraction = next;
            _frameWorkTimeFramesSinceChange = 0;
        }
        else _frameWorkTimeFramesSinceChange++;
    }

    private void CommitWorkTime()
    {
        if (!_workTimeFrames.TryGetValue(_frameIndex, out WorkTimeFrame? frame)) return;
        _workTimeFraction = _frameWorkTimeFraction;
        _workTimeFramesSinceChange = _frameWorkTimeFramesSinceChange;
        if (_frameWorkTimeClearsHistory) _workTimeSamples.Clear();
        frame.Complete = true;
        AcceptWorkTime(_frameIndex, frame);
    }

    private void AcceptWorkTime(ulong tag, WorkTimeFrame frame)
    {
        if (!frame.Complete || frame.Milliseconds is not { } milliseconds) return;
        _workTimeFrames.Remove(tag);
        if (_ignoreFirstWorkTime) { _ignoreFirstWorkTime = false; return; }
        if (_workTimeSamples.Count == 16) _workTimeSamples.RemoveAt(0);
        _workTimeSamples.Add(new WorkTimeSample(milliseconds, frame.Fraction));
    }

    private sealed class WorkTimeReceiver(VirtualShadowFeature feature) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            if (!feature._workTimeFrames.TryGetValue(tag, out WorkTimeFrame? frame)) return;
            ReadOnlySpan<ulong> timestamps = MemoryMarshal.Cast<byte, ulong>(bytes);
            frame.Milliseconds = (float)(1000.0 * (timestamps[1] - timestamps[0]) / feature._workTimeFrequency);
            feature.AcceptWorkTime(tag, frame);
        }
    }


    // Optional four-frame diagnostic. Results never feed shadow quality or scheduling.
    private readonly GraphRegionId _invalidationTimingRegion;
    private QueryPool? _invalidationTimingQueries;
    private GraphQueryPoolId _invalidationTimingQueryId;
    private GraphPassId _invalidationTimingResolvePass;
    private GraphReadbackId _invalidationTimingReadback;
    private ulong _invalidationTimingFrequency;
    private int? _requestedInvalidationTimingFrame;
    private Dictionary<ulong, InvalidationTimingFrame>? _invalidationTimingFrames;

    private sealed class InvalidationTimingFrame(int simulationFrame, uint changed, uint deferred, uint views)
    {
        internal readonly int SimulationFrame = simulationFrame;
        internal readonly uint Changed = changed, Deferred = deferred, Views = views;
        internal bool Complete;
        internal ulong? Begin, End;
    }

    /// <summary>Enables explicit GPU timing of caster invalidation without changing shadow policy.</summary>
    public void EnableInvalidationTiming()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_invalidationTimingQueries is not null) return;
        ulong frequency = _backend.CalibrateTimestamps(_backend.GetQueue(_device, QueueType.Graphics)).QueueFrequency;
        if (frequency == 0) throw new InvalidOperationException("The graphics timestamp frequency is zero.");
        QueryPool queries = _backend.CreateQueryPool(_device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 2, Label: "Caster invalidation diagnostic"));
        try
        {
            if (queries.ResultInfo.ResultStride != sizeof(ulong))
                throw new NotSupportedException("Caster timing requires one 64-bit value per timestamp.");
            RenderGraphEdit edit = _renderGraph.BeginEdit();
            try
            {
                GraphQueryPoolId id = edit.RegisterQueryPool(queries);
                GraphBufferId values = edit.CreateBuffer(new BufferDesc(2u * sizeof(ulong),
                    BufferUsages.QueryResolve | BufferUsages.CopyDestination | BufferUsages.CopySource,
                    "Caster invalidation timestamp values"));
                GraphPassId resolve = edit.AddGeneralFramePass("Resolve caster invalidation diagnostic",
                    PassQueueSelection.AnyOfType(QueueType.Graphics), new WorkTimestampResults(id, values),
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    WorkTimestampResults.Declare, WorkTimestampResults.Record, region: _invalidationTimingRegion);
                edit.OrderAfter(resolve, _invalidatePass);
                edit.OrderAfter(_frameCompletePass, resolve);
                GraphReadbackId readback = edit.AddReadback(values, BufferRange.Whole,
                    new InvalidationTimingReceiver(this), "Read caster invalidation diagnostic", _invalidationTimingRegion);
                edit.OrderAfter(readback, resolve);
                edit.Commit();
                _invalidationTimingQueryId = id;
                _invalidationTimingResolvePass = resolve;
                _invalidationTimingReadback = readback;
                _invalidationTimingFrequency = frequency;
                _invalidationTimingFrames = [];
                _invalidationTimingQueries = queries;
            }
            finally { edit.Dispose(); }
        }
        catch { queries.Dispose(); throw; }
    }

    /// <summary>Requests one timestamp pair for the next ordinary staged frame.</summary>
    public void RequestInvalidationTiming(int simulationFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(simulationFrame);
        if (_invalidationTimingQueries is null) throw new InvalidOperationException("Enable caster timing first.");
        if (_requestedInvalidationTimingFrame is not null) throw new InvalidOperationException("Caster timing was already requested.");
        _requestedInvalidationTimingFrame = simulationFrame;
    }

    private GraphPassId AddInvalidationPass(ref RenderGraphEdit edit, GraphRegionId region) =>
        edit.AddComputeFramePass("Invalidate moved virtual shadow casters", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new InvalidationDispatch(default), new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            _shadowMap.CasterInvalidationPipeline.Pipeline, _shadowMap.CasterInvalidationPipeline.ParameterLayout, default,
            InvalidationDispatch.Declare, InvalidationDispatch.Record, region: region);

    private void StageInvalidationTiming(ref RenderGraphFrame graph)
    {
        if (_invalidationTimingQueries is null) return;
        bool enabled = _requestedInvalidationTimingFrame is not null && _viewCount != 0;
        graph.SetPassEnabled(_invalidationTimingResolvePass, enabled);
        graph.SetReadbackEnabled(_invalidationTimingReadback, enabled);
        if (enabled)
        {
            _invalidationTimingFrames!.Add(_frameIndex, new InvalidationTimingFrame(
                _requestedInvalidationTimingFrame!.Value, checked((uint)_changedInstances.Count),
                checked((uint)(_streamingChangedInstances.Count + _lodCasterBounds.Count / 2)), checked((uint)_viewCount)));
            // Frame command-data updates do not rerun the initial resource declaration.
            PassDefinition invalidation = graph.GetPass(_invalidatePass);
            invalidation.Write(_invalidationTimingQueryId, new QueryRange(0, 2));
            graph.SetPassDataIfChanged(_invalidatePass, new InvalidationDispatch(
                new DispatchArguments(Math.Min(checked((uint)_changedInstances.Count), 65535u),
                    DivideRoundUp(checked((uint)_changedInstances.Count), 65535u), 1u), _invalidationTimingQueryId));
            graph.SetReadbackTag(_invalidationTimingReadback, _frameIndex);
        }
        _requestedInvalidationTimingFrame = null;
    }

    private void FinishInvalidationTiming(RenderGraphFrameResult result, bool complete)
    {
        if (_invalidationTimingFrames is null ||
            !_invalidationTimingFrames.TryGetValue(_frameIndex, out InvalidationTimingFrame? frame)) return;
        if (!complete || !result.TryGetPassCompletion(_invalidationTimingResolvePass, out _))
        {
            _invalidationTimingFrames.Remove(_frameIndex);
            return;
        }
        frame.Complete = true;
        ReportInvalidationTiming(_frameIndex, frame);
    }

    private void ReportInvalidationTiming(ulong tag, InvalidationTimingFrame frame)
    {
        if (!frame.Complete || frame.Begin is not { } begin || frame.End is not { } end) return;
        _invalidationTimingFrames!.Remove(tag);
        if (end < begin) throw new InvalidDataException("Graphics timestamps were not ordered within one queue.");
        double milliseconds = 1000.0 * (end - begin) / _invalidationTimingFrequency;
        Console.WriteLine(FormattableString.Invariant(
            $"VSM_INVALIDATION_GPU {{\"SimulationFrame\":{frame.SimulationFrame},\"ShadowFrame\":{tag},\"ChangedInstances\":{frame.Changed},\"DeferredInstances\":{frame.Deferred},\"Views\":{frame.Views},\"Queue\":\"Graphics\",\"QueueFrequency\":{_invalidationTimingFrequency},\"BeginTicks\":{begin},\"EndTicks\":{end},\"Milliseconds\":{milliseconds:R}}}"));
    }

    private sealed class InvalidationTimingReceiver(VirtualShadowFeature feature) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            if (feature._invalidationTimingFrames is null ||
                !feature._invalidationTimingFrames.TryGetValue(tag, out InvalidationTimingFrame? frame)) return;
            if (bytes.Length != 2 * sizeof(ulong)) throw new InvalidDataException("Caster timestamp readback size changed.");
            ReadOnlySpan<ulong> values = MemoryMarshal.Cast<byte, ulong>(bytes);
            frame.Begin = values[0];
            frame.End = values[1];
            feature.ReportInvalidationTiming(tag, frame);
        }
    }

    private readonly record struct InvalidationDispatch(DispatchArguments Dispatch, GraphQueryPoolId Queries = default)
    {
        internal static void Declare(ref PassDefinition pass, ref InvalidationDispatch data)
        {
            if (data.Queries != default) pass.Write(data.Queries, new QueryRange(0, 2));
        }

        internal static void Record(ref ComputePassCommandScope commands, in InvalidationDispatch data)
        {
            if (data.Queries != default) commands.WriteTimestamp(data.Queries, 0);
            commands.Dispatch(data.Dispatch);
            if (data.Queries != default) commands.WriteTimestamp(data.Queries, 1);
        }
    }

    private readonly record struct WorkTimestamp(GraphQueryPoolId Queries, uint Index)
    {
        public static void Declare(ref PassDefinition pass, ref WorkTimestamp data) => pass.Write(data.Queries, new QueryRange(data.Index, 1));
        public static void Record(ref GeneralPassCommandScope commands, in WorkTimestamp data) => commands.WriteTimestamp(data.Queries, data.Index);
    }

    private readonly record struct WorkTimestampResults(GraphQueryPoolId Queries, GraphBufferId Results)
    {
        public static void Declare(ref PassDefinition pass, ref WorkTimestampResults data)
        {
            pass.Read(data.Queries, new QueryRange(0, 2));
            _ = pass.Write(data.Results, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }
        public static void Record(ref GeneralPassCommandScope commands, in WorkTimestampResults data) =>
            commands.ResolveQueries(data.Queries, 0, 2, commands.GetBuffer(data.Results), new BufferRange(0, 2u * sizeof(ulong)));
    }
}
