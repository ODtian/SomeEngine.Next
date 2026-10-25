namespace SomeEngine.Runtime.Tests;

public sealed class ApplicationViewTests
{
    [Fact]
    public void Registering_views_returns_distinct_image_handles_and_removing_one_preserves_the_other()
    {
        var application = new ApplicationScene(".");
        ApplicationView first = application.AddView(new(8));
        ApplicationView second = application.AddView(new(9));
        Assert.NotEqual(first.ImageId, second.ImageId);
        Assert.True(application.RemoveView(first));
        Assert.False(application.RemoveView(first));
        Assert.Equal([second], application.Views);
    }

    [Fact]
    public void Resizing_a_view_preserves_its_public_image_handle()
    {
        var application = new ApplicationScene(".");
        ApplicationView view = application.AddView(new(9, 32, 24));
        nint image = view.ImageId;
        view.Settings = new(10, 64, 48);
        Assert.Equal(image, view.ImageId);
        Assert.Equal(new ApplicationViewSettings(10, 64, 48), view.Settings);
    }

    [Fact]
    public void Clearing_views_does_not_reuse_a_removed_image_handle()
    {
        var application = new ApplicationScene(".");
        ApplicationView first = application.AddView(new(9));
        application.ClearViews();
        Assert.Empty(application.Views);
        ApplicationView replacement = application.AddView(new(9));
        Assert.NotEqual(first.ImageId, replacement.ImageId);
    }

    [Fact]
    public void Unsupported_diagnostic_modes_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>("mode", () => new ApplicationViewSettings(7));
        Assert.Throws<ArgumentOutOfRangeException>("mode", () => new ApplicationViewSettings(12));
    }

    [Fact]
    public void View_dimensions_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>("width", () => new ApplicationViewSettings(9, 0, 16));
        Assert.Throws<ArgumentOutOfRangeException>("height", () => new ApplicationViewSettings(9, 16, 0));
    }
}
