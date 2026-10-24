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
        GraphBufferId results = edit.CreateBuffer(new BufferDesc(2u * sizeof(ulong), BufferUsages.CopyDestination | BufferUsages.CopySource,
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
