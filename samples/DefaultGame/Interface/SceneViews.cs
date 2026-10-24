using SomeEngine.ECS;
using SomeEngine.Elements.Render;
using SomeEngine.Render.Systems;
using SomeEngine.Runtime;

namespace DefaultGame;

internal sealed class SceneViews : IDisposable
{
    private readonly ScreenText? _text;
    private readonly SurfacePanelExample? _panel;
    private readonly HudOverlay? _hud;
    private readonly SphereNameplates? _nameplates;
    private readonly bool _changeNumbers;

    internal SceneViews(World world, RenderExtractionSystems extraction, int width, int height,
        RuntimeStartupOptions options)
    {
        _changeNumbers = options.HudNumbersChange;
        if (options.SurfacePanel || options.HudOverlay)
        {
            _text = new ScreenText(world);
            extraction.Add(new GlyphRenderExtractor(_text.AtlasSource));
        }
        if (options.SurfacePanel) _panel = new SurfacePanelExample(world, width, height, _text!);
        if (options.HudOverlay) _hud = new HudOverlay(world, width, height, _text!);
        if (options.WorldNameplates) _nameplates = new SphereNameplates(world);
    }

    internal void Update(int frame, int width, int height)
    {
        _panel?.Resize(width, height);
        _hud?.Resize(width, height);
        if (_changeNumbers) _hud?.Update(frame);
        _nameplates?.Update();
    }

    public void Dispose()
    {
        _nameplates?.Dispose();
        _hud?.Dispose();
        _panel?.Dispose();
        _text?.Dispose();
    }
}
