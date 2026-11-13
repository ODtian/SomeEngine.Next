using System.Text;
using System.Text.Json;

namespace SomeEngine.Assets.Importers;

internal static partial class MaterialShaderEntries
{
    private static void AppendRasterEntries(StringBuilder text, string encoding, string bounds,
        string screenFaces, string shadowFaces, bool tessellated)
    {
        text.AppendLine("namespace MaterialRasterEntries");
        text.AppendLine("{");
        text.AppendLine("typedef BakedMaterial.Vertex VertexEvaluate;");
        text.AppendLine("typedef BakedMaterial.Domain DomainEvaluate;");
        text.AppendLine("typedef BakedMaterial.Visibility VisibilityEvaluate;");
        text.AppendLine("struct SurfaceEvaluate : ISurfaceEvaluate<DomainEvaluate.Surface,DomainEvaluate.TriangleData>");
        text.AppendLine("{");
        text.AppendLine("    BakedMaterial Surface;");
        text.AppendLine("    void evaluateSurface<TOutput:ISurfaceOutput>(SurfacePixelInput input,DomainEvaluate.Surface surface,");
        text.AppendLine("        float3 bary,DomainEvaluate.TriangleData triangle,TOutput output)");
        text.AppendLine("    { Surface.evaluateSurface<TOutput>(input,surface,bary,triangle,output); }");
        text.AppendLine("    void evaluateDebug<TOutput:ISurfaceOutput>(uint mode,SurfacePixelInput input,DomainEvaluate.Surface surface,");
        text.AppendLine("        float3 bary,DomainEvaluate.TriangleData triangle,TOutput output)");
        text.AppendLine("    { Surface.evaluateDebug<TOutput>(mode,input,surface,bary,triangle,output); }");
        text.AppendLine("};");
        text.Append("#define CLUSTER_VERTEX_ENCODING ").AppendLine(JsonSerializer.Serialize(encoding));
        text.Append("#define CLUSTER_RASTER_BOUNDS ").AppendLine(JsonSerializer.Serialize(bounds));
        text.Append("#define CLUSTER_TESSELLATION_BOUNDS ").AppendLine(JsonSerializer.Serialize(bounds));
        text.Append("#define CLUSTER_RASTER_TWO_SIDED ").AppendLine(FaceValue(screenFaces));
        text.Append("#define CLUSTER_TARGET_RASTER_TWO_SIDED ").AppendLine(FaceValue(shadowFaces));
        text.Append("#include \"").Append(RasterEntriesHeader).AppendLine("\"");
        if (tessellated) text.Append("#include \"").Append(TessellationEntriesHeader).AppendLine("\"");
        text.AppendLine("}");
        text.AppendLine("typedef MaterialRasterEntries::SurfaceEvaluate SurfaceEvaluate;");
    }

    private static string FaceValue(string faces) => faces switch
    {
        "front" => "0",
        "both" => "1",
        _ => throw new InvalidDataException($"RasterFaces has unsupported value '{faces}'."),
    };
}
