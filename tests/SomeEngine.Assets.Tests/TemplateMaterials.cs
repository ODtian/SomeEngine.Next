using System.Runtime.CompilerServices;
internal static class TemplateMaterials {
    [ModuleInitializer] internal static void Register() => Game.Assets.MaterialTypes.Register();
}
