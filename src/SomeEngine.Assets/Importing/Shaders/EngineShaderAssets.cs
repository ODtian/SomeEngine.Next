using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

/// <summary>GUID references to shared engine shaders used directly by rendering features.</summary>
public static class EngineShaderAssets
{
    public static AssetId<ShaderAsset> GpuPose => new(new Guid("3b11266f-4a57-4438-8dc6-77b4fcb984db"));
    public static AssetId<ShaderAsset> SkinBounds => new(new Guid("a4282992-977f-417e-a37c-38787cc8f7ae"));
    public static AssetId<ShaderAsset> CanvasSurface => new(new Guid("c58c6484-ae22-4f1a-969f-1c328c8a84e2"));
    public static AssetId<ShaderAsset> CanvasImage => new(new Guid("9802956d-b78a-4f23-9b56-9c982b8b88bb"));
    public static AssetId<ShaderAsset> CanvasGlyphs => new(new Guid("e57f54ed-4a66-4c2c-ada6-98e72747d12c"));
    public static AssetId<ShaderAsset> RenderStencil => new(new Guid("84721aac-639b-4e3b-aa30-a3dcc5eae1a8"));
    public static AssetId<ShaderAsset> LayerFragmentStore => new(new Guid("fb626218-8fc1-438c-a48f-14aa09805686"));
    public static AssetId<ShaderAsset> LayerComposite => new(new Guid("156c1873-8e19-4847-a7b6-faa2d34c66e7"));
    public static AssetId<ShaderAsset> LayerClusterBinning => new(new Guid("a8d6aa04-5086-4cae-9434-89abddbd5577"));
    public static AssetId<ShaderAsset> LayerTileBinning => new(new Guid("2437106e-a633-4599-8d3c-8159eb22115c"));
    public static AssetId<ShaderAsset> LayerStencilTransfer => new(new Guid("37c64ae0-3a30-4e6c-a707-7ef35102af51"));
}
