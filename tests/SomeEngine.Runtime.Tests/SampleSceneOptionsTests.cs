using DefaultGame;

namespace SomeEngine.Runtime.Tests;

public sealed class SampleSceneOptionsTests
{
    [Fact]
    public void Defaults_enable_dynamic_scene_and_changing_hud_numbers_without_example_interfaces()
    {
        SampleSceneOptions options = SampleSceneOptions.Parse([]);

        Assert.True(options.DynamicScene);
        Assert.True(options.HudNumbersChange);
        Assert.Null(options.ScenePlayback);
        Assert.False(options.SurfacePanel);
        Assert.False(options.WorldNameplates);
        Assert.False(options.HudOverlay);
    }

    [Theory]
    [InlineData("--static-scene", "--DYNAMIC-SCENE", true)]
    [InlineData("--dynamic-scene", "--STATIC-SCENE", false)]
    [InlineData("--dynamic-scene", "--NO-DYNAMIC-SCENE", false)]
    public void The_last_dynamic_scene_switch_selects_whether_meshes_animate(string first, string last, bool expected)
    {
        SampleSceneOptions options = SampleSceneOptions.Parse([first, last]);

        Assert.Equal(expected, options.DynamicScene);
    }

    [Theory]
    [InlineData("motion")]
    [InlineData("materials")]
    [InlineData("camera")]
    public void Playback_accepts_separate_and_equals_values(string value)
    {
        SampleSceneOptions separate = SampleSceneOptions.Parse(["--SCENE-PLAYBACK", value]);
        SampleSceneOptions equals = SampleSceneOptions.Parse([$"--SCENE-PLAYBACK={value}"]);

        Assert.Equal(value, separate.ScenePlayback);
        Assert.Equal(separate, equals);
    }

    [Fact]
    public void The_first_playback_value_is_kept_when_the_option_repeats()
    {
        SampleSceneOptions options = SampleSceneOptions.Parse(["--scene-playback=motion", "--scene-playback", "camera"]);

        Assert.Equal("motion", options.ScenePlayback);
    }

    [Fact]
    public void A_missing_playback_value_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => SampleSceneOptions.Parse(["--scene-playback"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("MOTION")]
    [InlineData("unknown")]
    public void Unsupported_playback_values_are_rejected(string value)
    {
        Assert.Throws<ArgumentException>(() => SampleSceneOptions.Parse([$"--scene-playback={value}"]));
    }

    [Fact]
    public void Example_interface_switches_ignore_case()
    {
        SampleSceneOptions options = SampleSceneOptions.Parse(["--SURFACE-PANEL", "--WORLD-NAMEPLATES", "--HUD"]);

        Assert.True(options.SurfacePanel);
        Assert.True(options.WorldNameplates);
        Assert.True(options.HudOverlay);
        Assert.True(options.HudNumbersChange);
    }

    [Fact]
    public void Static_hud_keeps_numbers_unchanged_regardless_of_hud_switch_order()
    {
        SampleSceneOptions first = SampleSceneOptions.Parse(["--HUD-STATIC", "--hud"]);
        SampleSceneOptions last = SampleSceneOptions.Parse(["--hud", "--HUD-STATIC"]);

        Assert.True(first.HudOverlay);
        Assert.False(first.HudNumbersChange);
        Assert.Equal(first, last);
    }
}
