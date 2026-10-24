using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>How an image fits the content rectangle supplied by layout.</summary>
public enum ImageFit : byte { Contain, Stretch, Cover }

/// <summary>An asset-backed image measured from its ready texture's intrinsic size.</summary>
public record struct Image(AssetId<TextureAsset> Source, ImageFit Fit = ImageFit.Contain)
    : IView, IComponent, ILayout
{
    public BuildTask Build()
    {
        Scope.Entity.Accessible(Role.Image, "");
        return default;
    }

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        DevelopmentCheck.Require(Fit is ImageFit.Contain or ImageFit.Stretch or ImageFit.Cover,
            "Image fitting requires Contain, Stretch or Cover.");
        DevelopmentCheck.Require(pass.Has<ImageTexture>(pass.Node),
            "Image measurement requires ImageTextures registered with LayoutSystem.");
        return constraints.Constrain(pass.Read<ImageTexture>(pass.Node).Size);
    }
}
