using System.Text;

namespace SomeEngine.Assets.Importers;

internal static partial class MaterialShaderEntries
{
    private static void AppendLayerHardwareEntries(StringBuilder text, string encoding, string bounds,
        string screenFaces, string shadowFaces, bool tessellated = false)
    {
        AppendGeometryAttributes(text, encoding, bounds, screenFaces, shadowFaces);
        text.AppendLine("[RasterMaterialInputs(\"evaluators\")][shader(\"vertex\")]");
        text.AppendLine("ClusterRasterPixelInput<BakedMaterial.Visibility.Varying> LayerRasterVertex(");
        text.AppendLine("    uniform BakedMaterial.Vertex vertex,");
        if (tessellated) text.AppendLine("    uniform BakedMaterial.Domain domain,");
        text.AppendLine("    uniform BakedMaterial.Visibility visibility,");
        text.AppendLine(tessellated
            ? "    uint vertexID:SV_VertexID,uint patchIndex:ATTRIBUTE0)"
            : "    uint vertexID:SV_VertexID,uint instanceID:SV_InstanceID)");
        text.AppendLine(tessellated
            ? "{ return MaterialRasterEntries::EvaluateTessellationHardwareVertex(vertex,domain,visibility,vertexID,patchIndex); }"
            : "{ return BuildLayerHardwareVertex(vertex,visibility,vertexID,instanceID); }");
        foreach ((string entry, string requests) in new[]
                 { ("LayerRasterPixel", "LayerHardwareShadowPageRequests"), ("LayerRasterPixelNoRequests", "LayerNoPageRequests") })
        {
            AppendGeometryAttributes(text, encoding, bounds, screenFaces, shadowFaces);
            text.AppendLine("[RasterMaterialInputs(\"evaluators\")][shader(\"fragment\")]");
            text.Append("void ").Append(entry).AppendLine("(uniform BakedMaterial.Visibility visibility,");
            text.AppendLine("    ClusterRasterPixelInput<BakedMaterial.Visibility.Varying> input,bool frontFace:SV_IsFrontFace)");
            text.AppendLine("{");
            if (screenFaces == "front") text.AppendLine("    if(!frontFace)return;");
            text.Append("    WriteLayerHardwareVisibility<BakedMaterial.Vertex.Vertex,BakedMaterial.Visibility,")
                .Append(requests).AppendLine(">(visibility,input,frontFace);");
            text.AppendLine("}");
        }
    }
}
