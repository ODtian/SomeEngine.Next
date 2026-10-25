namespace SomeEngine.Assets.Schema;

/// <summary>Engine material and interface IR distributed for shader asset preparation.</summary>
public static class StandardMaterialInputs
{
    public static ShaderSourceBlock Create()
    {
        var result = new ShaderSourceBlock { SourceNames = ["material_interfaces.slang", "standard_material.slang"] };
        result.SetBytes(ShaderInputBlock.SerializedSourceKey(0), Read("material_interfaces"));
        result.SetBytes(ShaderInputBlock.SerializedSourceKey(1), Read("standard_material"));
        return result;
    }

    /// <summary>Creates the reusable UV0 alpha-mask evaluator for a caller-selected shader variant.</summary>
    /// <remarks>The cutoff is already adjusted for the material alpha factor; projects retain variant identity and wiring.</remarks>
    public static string CreateAlphaMaskSource(float cutoff, bool trackDepthInputs = false)
    {
        if (!float.IsFinite(cutoff) || cutoff < 0)
            throw new ArgumentOutOfRangeException(nameof(cutoff), "A mask cutoff must be finite and nonnegative.");
        string declaration = trackDepthInputs ? "[MaterialDepthInputs(\"AlbedoMap,MaterialSampler\")]\n" : "";
        return "struct StandardAlphaMaskVarying { float2 UV : TEXCOORD0; };\n" + declaration + """
[VisibilityDepth("masked")]
struct StandardAlphaMask : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = StandardAlphaMaskVarying;
    Texture2D<float4> AlbedoMap;
    SamplerState MaterialSampler;
    Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input)
    { Varying result; result.UV = vertex.uv0; return result; }
    Varying readLane(Varying value, uint lane)
    { Varying result; result.UV = WaveReadLaneAt(value.UV, lane); return result; }
    Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary)
    { Varying result; result.UV = a.UV * bary.x + b.UV * bary.y + c.UV * bary.z; return result; }
    Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input)
    { return interpolateVertex(a, b, c, input.PerspectiveBarycentric); }
    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        VisibilityPixelResult result;
        result.Visible = AlbedoMap.SampleLevel(MaterialSampler, varying.UV, 0.0).a >= ALPHA_CUTOFF ? 1u : 0u;
        result.Depth = input.Depth;
        return result;
    }
};
""".Replace("ALPHA_CUTOFF", cutoff.ToString("R", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    internal static byte[] Read(string sourceName)
    {
        using Stream source = typeof(StandardMaterialInputs).Assembly.GetManifestResourceStream(
            "SomeEngine.Assets.Shaders." + sourceName + ".slang-module")!;
        var bytes = new byte[checked((int)source.Length)];
        source.ReadExactly(bytes);
        return bytes;
    }
}
