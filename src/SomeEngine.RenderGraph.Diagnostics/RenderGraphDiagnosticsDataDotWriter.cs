using System.Text;

namespace SomeEngine.RenderGraph.Diagnostics;

public static class RenderGraphDiagnosticsDataDotWriter
{
    public static string Write(RenderGraphDiagnosticsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var text = new StringBuilder("digraph RenderGraph {\n  rankdir=LR;\n");
        foreach (RenderGraphDiagnosticsData.Pass pass in data.Passes)
        {
            string style = pass.Live ? "solid" : "dashed";
            text.Append("  p").Append(pass.Ordinal)
                .Append(" [label=\"").Append(Escape(pass.Label)).Append("\", style=")
                .Append(style).Append("];\n");
        }
        foreach (RenderGraphDiagnosticsData.Dependency dependency in data.Dependencies)
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
