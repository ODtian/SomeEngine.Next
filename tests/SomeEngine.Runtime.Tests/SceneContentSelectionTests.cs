using DefaultGame;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Runtime.Tests;

public sealed class SceneContentSelectionTests
{
    [Fact]
    public void Selecting_a_scene_updates_its_pack_order_and_application_identity_together()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var platform = new SceneTestPlatform([
            new TestScene("first", ".") { AssetPacks = ["patch.pack", "base.pack"], ApplicationAsset = first },
            new TestScene("second", ".") { AssetPacks = ["other.pack"], ApplicationAsset = second },
        ]);
        Assert.Equal(["patch.pack", "base.pack"], platform.Application.AssetPacks);
        Assert.Equal(new AssetId<ApplicationSettingsAsset>(first), platform.Application.ApplicationAsset);
        platform.SelectScene(1);
        Assert.Equal(["other.pack"], platform.Application.AssetPacks);
        Assert.Equal(new AssetId<ApplicationSettingsAsset>(second), platform.Application.ApplicationAsset);
    }

    [Fact]
    public void Diagnostic_view_arguments_preserve_ordered_clear_and_both_value_spellings()
    {
        var application = new ApplicationScene(".");
        SceneTestPlatform.ConfigureViews(application,
            ["--debug-view=8:16x24", "--no-debug-views", "--debug-view", "9:32X48", "--debug-view=10:64x96"]);
        Assert.Equal([new ApplicationViewSettings(9, 32, 48), new ApplicationViewSettings(10, 64, 96)],
            application.Views.Select(view => view.Settings));
    }

    [Fact]
    public void Invalid_view_arguments_leave_the_registered_views_unchanged()
    {
        var application = new ApplicationScene(".");
        ApplicationView existing = application.AddView(new(9));
        Assert.Throws<ArgumentOutOfRangeException>("mode", () =>
            SceneTestPlatform.ConfigureViews(application, ["--debug-view=9:32x48", "--debug-view=7:32x48"]));
        Assert.Equal([existing], application.Views);
    }
}
