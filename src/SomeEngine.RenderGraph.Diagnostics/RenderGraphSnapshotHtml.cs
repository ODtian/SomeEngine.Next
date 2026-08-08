namespace SomeEngine.RenderGraph.Diagnostics;

using System.Net;
using System.Text;

public static class RenderGraphSnapshotHtml
{
    public static string Write(RenderGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StringBuilder html = new();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Render Graph</title>")
            .Append("<style>body{font:14px system-ui;margin:2rem;color:#202124}table{border-collapse:collapse;margin-bottom:2rem}th,td{border:1px solid #ccc;padding:.35rem .55rem;text-align:left}th{background:#f3f4f6}code{white-space:pre-wrap}</style>")
            .Append("</head><body><h1>Render Graph Snapshot</h1>")
            .Append("<p>status: ").Append(snapshot.Succeeded ? "succeeded" : "failed")
            .Append("; resources: ").Append(snapshot.Resources.Length)
            .Append("; passes: ").Append(snapshot.Passes.Length)
            .Append("; batches: ").Append(snapshot.Batches.Length).Append("</p>");

        html.Append("<h2>Passes</h2><table><thead><tr><th>#</th><th>Name</th><th>Queue</th><th>Flags</th><th>Live</th><th>Accesses</th></tr></thead><tbody>");
        foreach (RenderGraphSnapshot.Pass pass in snapshot.Passes)
        {
            html.Append("<tr><td>").Append(pass.Ordinal).Append("</td><td>")
                .Append(Encode(pass.Name)).Append("</td><td>").Append(pass.Queue)
                .Append("</td><td>").Append(pass.Flags).Append("</td><td>")
                .Append(pass.Live).Append("</td><td>").Append(pass.AccessCount).Append("</td></tr>");
        }
        html.Append("</tbody></table><h2>Resources</h2><table><thead><tr><th>#</th><th>Kind</th><th>Name</th><th>Live</th><th>Imported</th><th>Heap</th><th>Offset</th></tr></thead><tbody>");
        foreach (RenderGraphSnapshot.Resource resource in snapshot.Resources)
        {
            html.Append("<tr><td>").Append(resource.Ordinal).Append("</td><td>")
                .Append(resource.Kind).Append("</td><td>").Append(Encode(resource.Name))
                .Append("</td><td>").Append(resource.Live).Append("</td><td>")
                .Append(resource.Imported).Append("</td><td>").Append(resource.Heap)
                .Append("</td><td>").Append(resource.HeapOffset).Append("</td></tr>");
        }
        html.Append("</tbody></table><h2>Timings</h2><table><thead><tr><th>Name</th><th>Clock</th><th>Unit</th><th>Start</th><th>Close</th><th>Duration</th></tr></thead><tbody>");
        foreach (RenderGraphSnapshot.Timing timing in snapshot.Timings)
        {
            html.Append("<tr><td>").Append(Encode(timing.Name)).Append("</td><td>")
                .Append(timing.ClockDomain).Append("</td><td>").Append(timing.Unit)
                .Append("</td><td>").Append(timing.Start).Append("</td><td>")
                .Append(timing.Close).Append("</td><td>").Append(timing.Duration)
                .Append("</td></tr>");
        }
        return html.Append("</tbody></table></body></html>").ToString();
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
