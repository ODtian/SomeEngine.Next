using System.Net;
using System.Text;

namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphSnapshotHtmlWriter
{
    public static string Write(RenderGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = new StringBuilder();
        text.Append("<!doctype html><meta charset=\"utf-8\"><title>Render Graph</title>")
            .Append("<h1>Render Graph</h1><p>Structure version ")
            .Append(snapshot.StructureVersion)
            .Append("</p><table><thead><tr><th>Pass</th><th>Kind</th><th>Live</th><th>Queue</th></tr></thead><tbody>");
        foreach (RenderGraphSnapshot.Pass pass in snapshot.Passes)
        {
            text.Append("<tr><td>").Append(WebUtility.HtmlEncode(pass.Label))
                .Append("</td><td>").Append(pass.Kind)
                .Append("</td><td>").Append(pass.Live)
                .Append("</td><td>")
                .Append(pass.Queue.HasValue ? WebUtility.HtmlEncode(pass.Queue.Value.ToString()) : string.Empty)
                .Append("</td></tr>");
        }
        return text.Append("</tbody></table>").ToString();
    }
}
