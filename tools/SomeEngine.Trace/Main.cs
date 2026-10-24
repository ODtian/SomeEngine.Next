using SomeEngine.Trace;
using System.Text.Json;

try
{
if (args.Length == 2 && args[0] == "benchmark")
{
    TraceBenchmark.Execute(args[1]);
    return 0;
}
if (args.Length >= 2 && args[0] is "inspect" or "analyze")
{
    TraceData data = TraceFile.Read(args[1]);
    Console.WriteLine($"{data.Events.Length} events, {data.Threads.Length} threads, " +
        $"{data.Threads.Sum(t => t.Dropped)} dropped events, {data.Misuse} scope errors.");
    if (args[0] == "inspect")
    {
        if (args.Length != 2 && !(args.Length == 4 && args[2] == "--span"))
            throw new ArgumentException("inspect accepts only an optional --span id.");
        if (args.Length == 4 && args[2] == "--span")
        {
            long id = long.Parse(args[3]);
            var spans = TraceAnalyzer.Analyze(data).Spans;
            foreach (TraceSpan span in spans.Where(s => s.Id == id || s.Parent == id).OrderBy(s => s.Start))
                Console.WriteLine($"id={span.Id} parent={span.Parent} thread={span.Thread} frame={span.Frame} " +
                    $"ticks={span.Start}..{span.End} self={span.SelfTicks} {span.Site.Category}/{span.Site.Name}");
        }
        return 0;
    }
    int warmup = 0;
    string? json = null;
    for (int index = 2; index < args.Length; index++)
    {
        string option = args[index];
        if (++index == args.Length) throw new ArgumentException($"{option} needs a value.");
        switch (option)
        {
            case "--warmup": warmup = int.Parse(args[index]); break;
            case "--json": json = args[index]; break;
            default: throw new ArgumentException($"Unknown option: {option}");
        }
    }
    TraceAnalysis analysis = TraceAnalyzer.Analyze(data, warmup);
    if (json is not null)
        File.WriteAllText(json, JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Accounting complete: {analysis.Complete}; {analysis.Frames.Length} frames after {analysis.SkippedFrames} warmup frames.");
    foreach (string issue in analysis.Issues) Console.Error.WriteLine(issue);
    if (analysis.Frames.Length > 0)
    {
        double[] times = analysis.Frames.Select(f => f.Milliseconds).Order().ToArray();
        Console.WriteLine($"Frame elapsed mean {times.Average():F4} ms; p50 {times[(times.Length - 1) / 2]:F4} ms; p95 {times[(int)Math.Ceiling(times.Length * 0.95) - 1]:F4} ms.");
        if (analysis.Metadata.GetValueOrDefault("trace.scopes") != "False")
            Console.WriteLine($"Mean worker thread elapsed: {analysis.Frames.Average(f => f.WorkerThreadMilliseconds):F4} ms; " +
                $"outside attributed frame: {analysis.Frames.Average(f => f.OutsideFrameMilliseconds):F4} ms.");
        else
            Console.WriteLine("Frame-only capture: operation costs, worker work and work outside frames were not measured.");
    }
    Console.WriteLine("Exclusive elapsed totals by category (parallel thread times are separate from frame latency):");
    foreach (var group in analysis.Costs.GroupBy(c => c.Category).OrderByDescending(g => g.Sum(c => c.SelfMilliseconds)))
        Console.WriteLine($"  {group.Key,-24} {group.Sum(c => c.SelfMilliseconds),12:F4} ms");
    foreach (var group in analysis.Counters.GroupBy(c => (c.Site.Category, c.Site.Name)))
        Console.WriteLine($"Counter {group.Key.Category}/{group.Key.Name}: {group.Min(c => c.Value)}..{group.Max(c => c.Value)} ({group.Count()} samples).");
    return analysis.Complete ? 0 : 3;
}
Console.Error.WriteLine("Usage: SomeEngine.Trace inspect <capture.setrace> [--span id] | analyze <capture.setrace> [--warmup frames] [--json report.json] | benchmark <new-output-directory>");
return 2;
}
catch (Exception error) when (error is IOException or ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
