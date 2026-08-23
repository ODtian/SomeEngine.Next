using System.Text;

namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphSnapshotDotWriter
{
    public static string Write(RenderGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = new StringBuilder("digraph RenderGraph {\n  rankdir=LR;\n");
        foreach (RenderGraphSnapshot.Pass pass in snapshot.Passes)
        {
            string style = pass.Live ? "solid" : "dashed";
            text.Append("  p").Append(pass.Ordinal)
                .Append(" [label=\"").Append(Escape(pass.Label)).Append("\", style=")
                .Append(style).Append("];\n");
        }
        foreach (RenderGraphSnapshot.Dependency dependency in snapshot.Dependencies)
        {
            text.Append("  p").Append(dependency.Predecessor)
                .Append(" -> p").Append(dependency.Consumer)
                .Append(" [label=\"").Append(dependency.Kind).Append("\"];\n");
        }
        return text.Append("}\n").ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
