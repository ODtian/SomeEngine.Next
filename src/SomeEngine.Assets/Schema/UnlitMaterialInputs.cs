namespace SomeEngine.Assets.Schema;

/// <summary>The engine's Unlit material and common interfaces as serialized Slang inputs.</summary>
public static class UnlitMaterialInputs
{
    public static ShaderSourceBlock Create()
    {
        var result = new ShaderSourceBlock { SourceNames = ["material_interfaces.slang", "unlit_material.slang"] };
        result.SetSerializedSource(0, StandardMaterialInputs.Read("material_interfaces"));
        result.SetSerializedSource(1, StandardMaterialInputs.Read("unlit_material"));
        return result;
    }
}
