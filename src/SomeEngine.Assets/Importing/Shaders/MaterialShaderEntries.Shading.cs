using System.Text;

namespace SomeEngine.Assets.Importers;

internal static partial class MaterialShaderEntries
{
    private static void AppendForwardShadeEntry(StringBuilder text, string encoding, string bounds,
        string screenFaces, string shadowFaces)
    {
        AppendGeometryAttributes(text, encoding, bounds, screenFaces, shadowFaces);
        text.AppendLine("[shader(\"compute\")][numthreads(64,1,1)]");
        text.AppendLine("void ClusterForwardShade<let visibilityDepth:uint,let debugMode:uint>(");
        text.AppendLine("    uniform BakedMaterial.Vertex vertex, uniform BakedMaterial.Domain domain,");
        text.AppendLine("    uniform BakedMaterial surface, uint3 thread:SV_DispatchThreadID)");
        text.AppendLine("{");
        text.AppendLine("    VisibilityBufferInput<BakedMaterial.Vertex,BakedMaterial.Domain> input;");
        text.AppendLine("    OpaqueOutput output;");
        text.AppendLine("    ShadeSurface<BakedMaterial,VisibilityBufferInput<BakedMaterial.Vertex,BakedMaterial.Domain>,OpaqueOutput,debugMode>(");
        text.AppendLine("        surface,vertex,domain,input,output,thread);");
        text.AppendLine("}");
    }
}
