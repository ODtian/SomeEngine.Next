namespace SomeEngine.RenderGraph.Diagnostics;

using System.Text;

public static class RenderGraphSnapshotDot
{
    public static string Write(RenderGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StringBuilder dot = new("digraph RenderGraph {\n  rankdir=LR;\n");
        foreach (RenderGraphSnapshot.Pass pass in snapshot.Passes)
        {
            dot.Append("  p").Append(pass.Ordinal).Append(" [shape=box,label=\"")
                .Append(Escape($"{pass.Ordinal}: {pass.Name}\\n{pass.Queue}"))
                .Append(pass.Live ? "\"];\n" : "\",style=dashed];\n");
        }
        foreach (RenderGraphSnapshot.Resource resource in snapshot.Resources)
        {
            dot.Append("  r").Append(resource.Ordinal).Append(" [shape=ellipse,label=\"")
                .Append(Escape($"{resource.Ordinal}: {resource.Name ?? resource.Kind}"))
                .Append(resource.Live ? "\"];\n" : "\",style=dashed];\n");
        }
        foreach (RenderGraphSnapshot.Access access in snapshot.Accesses)
        {
            if (access.Flags is GraphAccess.Read or GraphAccess.ReadWrite)
                dot.Append("  r").Append(access.ResourceOrdinal).Append(" -> p").Append(access.PassOrdinal).Append(";\n");
            if (access.Flags is GraphAccess.Write or GraphAccess.ReadWrite)
                dot.Append("  p").Append(access.PassOrdinal).Append(" -> r").Append(access.ResourceOrdinal).Append(";\n");
        }
        return dot.Append("}\n").ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
