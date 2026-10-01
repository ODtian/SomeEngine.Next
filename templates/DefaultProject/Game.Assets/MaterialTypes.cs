using Game.Content;
using SomeEngine.Assets.Schema;

namespace Game.Assets;

public static class MaterialTypes
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        MaterialAsset.RegisterType<StandardMaterial, StandardMaterialAsset>(static id => new(id));
        MaterialAsset.RegisterType<UnlitMaterial, UnlitMaterialAsset>(static id => new(id));
        return true;
    });

    /// <summary>Called by this project's application or build tool before opening its material assets.</summary>
    public static void Register() => _ = Registered.Value;
}
