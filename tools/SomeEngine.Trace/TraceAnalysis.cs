using SomeEngine.Diagnostics;
using System.Text.Json.Serialization;

namespace SomeEngine.Trace;

public sealed record TraceSpan(long Id, long Parent, long Related, long Frame, int Thread,
    TraceSiteInfo Site, TraceEventKind Kind, long Start, long End, long Allocated)
{
    public long SelfTicks { get; internal set; } = End - Start;
    public long SelfAllocated { get; internal set; } = Allocated;
}

public sealed record TraceCost(string Category, string Name, int Thread, long Calls,
    double InclusiveMilliseconds, double SelfMilliseconds, long SelfAllocatedBytes);
public sealed record TraceFrame(long Id, int Thread, double Milliseconds, double MainThreadMilliseconds,
    double WorkerThreadMilliseconds, double WorkExtentMilliseconds, double OutsideFrameMilliseconds,
    TraceCost[] Costs);
public sealed record TraceCounter(long Frame, int Thread, long Timestamp, TraceSiteInfo Site, long Value);
public sealed record TraceAnalysis(bool Complete, long DroppedEvents, long Misuse, int IssueCount,
    string[] Issues, int SkippedFrames, TraceFrame[] Frames, TraceCost[] Costs, [property: JsonIgnore] TraceSpan[] Spans,
    TraceCounter[] Counters, Dictionary<string, string> Metadata);

/// <summary>Offline accounting. Thread elapsed time is never presented as sampled CPU time.</summary>
public static class TraceAnalyzer
{
    public static TraceAnalysis Analyze(TraceData data, int warmupFrames = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(warmupFrames);
        List<string> issues = [];
        int issueCount = 0;
        void Issue(string message) { issueCount++; if (issues.Count < 100) issues.Add(message); }
        long dropped = data.Threads.Sum(t => t.Dropped);
        if (dropped != 0) Issue($"Capture dropped {dropped} events.");
        if (data.Misuse != 0) Issue($"Capture contains {data.Misuse} scope misuse errors.");
        var sites = data.Sites.ToDictionary(s => s.Id);
        Dictionary<long, ThreadEvent> starts = [], ends = [];
        List<ThreadEvent> counters = [];
        foreach (ThreadEvent item in data.Events)
        {
            TraceEvent ev = item.Event;
            if (ev.Timestamp < data.Start || ev.Timestamp > data.Stop) Issue("Event outside capture clock bounds.");
            switch (ev.Kind)
            {
                case TraceEventKind.Begin:
                case TraceEventKind.Frame:
                    if (ev.Id <= 0 || !starts.TryAdd(ev.Id, item)) Issue($"Duplicate/invalid span start {ev.Id}.");
                    break;
                case TraceEventKind.End:
                    if (!ends.TryAdd(ev.Id, item)) Issue($"Duplicate span end {ev.Id}.");
                    break;
                case TraceEventKind.Counter:
                    if (!sites.ContainsKey(ev.Site)) Issue($"Unknown counter site {ev.Site}.");
                    counters.Add(item);
                    break;
                default: Issue($"Unknown event kind {ev.Kind}."); break;
            }
        }
        Dictionary<long, TraceSpan> spans = [];
        foreach ((long id, ThreadEvent item) in starts)
        {
            TraceEvent begin = item.Event;
            if (!ends.Remove(id, out ThreadEvent end)) { Issue($"Unclosed span {id}."); continue; }
            if (!sites.TryGetValue(begin.Site, out TraceSiteInfo? site)) { Issue($"Unknown site {begin.Site}."); continue; }
            if (end.Thread != item.Thread || end.Event.Timestamp < begin.Timestamp ||
                (data.Allocations && end.Event.Value < begin.Value))
            { Issue($"Invalid span interval {id}."); continue; }
            spans.Add(id, new TraceSpan(id, begin.Parent, begin.Related, begin.Frame, item.Thread, site,
                begin.Kind, begin.Timestamp, end.Event.Timestamp, end.Event.Value - begin.Value));
        }
        foreach (long id in ends.Keys) Issue($"End without begin {id}.");
        Dictionary<long, List<TraceSpan>> children = [];
        foreach (TraceSpan span in spans.Values)
        {
            if (span.Related != 0 && !starts.ContainsKey(span.Related)) Issue($"Missing causal source {span.Related}.");
            if (span.Parent == 0) continue;
            if (!spans.TryGetValue(span.Parent, out TraceSpan? parent)) { Issue($"Missing parent {span.Parent}."); continue; }
            if (parent.Thread != span.Thread || parent.Start > span.Start || parent.End < span.End)
            { Issue($"Execution nesting is invalid for span {span.Id}."); continue; }
            parent.SelfTicks -= span.End - span.Start;
            parent.SelfAllocated -= span.Allocated;
            if (!children.TryGetValue(parent.Id, out List<TraceSpan>? list)) children[parent.Id] = list = [];
            list.Add(span);
        }
        foreach (TraceSpan span in spans.Values)
        {
            if (span.SelfTicks < 0 || span.SelfAllocated < 0) Issue($"Negative exclusive cost for span {span.Id}.");
            if (!children.TryGetValue(span.Id, out List<TraceSpan>? list)) continue;
            list.Sort(static (left, right) => left.Start != right.Start
                ? left.Start.CompareTo(right.Start) : left.End.CompareTo(right.End));
            long previousEnd = span.Start;
            foreach (TraceSpan child in list)
            {
                // Calls shorter than one clock tick occupy an empty interval.
                if (child.Start == child.End) continue;
                if (child.Start < previousEnd) Issue($"Overlapping direct children of span {span.Id}.");
                previousEnd = Math.Max(previousEnd, child.End);
            }
        }
        TraceSpan[] frameSpans = spans.Values.Where(s => s.Kind == TraceEventKind.Frame).OrderBy(s => s.Start).ToArray();
        if (frameSpans.Select(s => s.Frame).Distinct().Count() != frameSpans.Length) Issue("Frame IDs are not unique.");
        TraceSpan[] selected = frameSpans.Skip(warmupFrames).ToArray();
        var grouped = spans.Values.GroupBy(s => s.Frame).ToDictionary(g => g.Key, g => g.ToArray());
        double ms = 1000d / data.Frequency;
        List<TraceFrame> frames = [];
        foreach (TraceSpan frame in selected)
        {
            TraceSpan[] members = grouped[frame.Frame];
            double outside = 0;
            foreach (TraceSpan span in members)
            {
                long inside = Overlap(span.Start, span.End, frame.Start, frame.End);
                if (children.TryGetValue(span.Id, out List<TraceSpan>? list))
                    foreach (TraceSpan child in list) inside -= Overlap(child.Start, child.End, frame.Start, frame.End);
                outside += (span.SelfTicks - inside) * ms;
            }
            frames.Add(new TraceFrame(frame.Frame, frame.Thread, (frame.End - frame.Start) * ms,
                members.Where(s => s.Thread == frame.Thread).Sum(s => s.SelfTicks) * ms,
                members.Where(s => s.Thread != frame.Thread).Sum(s => s.SelfTicks) * ms,
                (members.Max(s => s.End) - members.Min(s => s.Start)) * ms, outside, Costs(members, ms)));
        }
        // With no frame marks, expose all spans so non-rendering tools can use the same capture format.
        var selectedIds = selected.Select(s => s.Frame).ToHashSet();
        TraceSpan[] included = (frameSpans.Length == 0 ? spans.Values : spans.Values.Where(s => selectedIds.Contains(s.Frame))).ToArray();
        return new TraceAnalysis(issueCount == 0, dropped, data.Misuse, issueCount, [.. issues],
            Math.Min(warmupFrames, frameSpans.Length), [.. frames], Costs(included, ms), [.. spans.Values],
            counters.Where(c => sites.ContainsKey(c.Event.Site)).Select(c => new TraceCounter(
                c.Event.Frame, c.Thread, c.Event.Timestamp, sites[c.Event.Site], c.Event.Value)).ToArray(), data.Metadata);
    }

    private static long Overlap(long start, long end, long otherStart, long otherEnd)
        => Math.Max(0, Math.Min(end, otherEnd) - Math.Max(start, otherStart));

    private static TraceCost[] Costs(IEnumerable<TraceSpan> spans, double millisecondsPerTick)
        => spans.GroupBy(s => (s.Site.Category, s.Site.Name, s.Thread)).Select(g => new TraceCost(
            g.Key.Category, g.Key.Name, g.Key.Thread, g.LongCount(),
            g.Sum(s => s.End - s.Start) * millisecondsPerTick, g.Sum(s => s.SelfTicks) * millisecondsPerTick,
            g.Sum(s => s.SelfAllocated))).OrderByDescending(c => c.SelfMilliseconds).ToArray();
}
