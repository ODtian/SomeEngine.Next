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

    internal static byte[] Read(string sourceName)
    {
        using Stream source = typeof(StandardMaterialInputs).Assembly.GetManifestResourceStream(
            "SomeEngine.Assets.Shaders." + sourceName + ".slang-module")!;
        var bytes = new byte[checked((int)source.Length)];
        source.ReadExactly(bytes);
        return bytes;
    }
}
