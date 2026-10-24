using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements.Render;
using SomeEngine.Typography;

namespace DefaultGame;

/// <summary>The font and glyph atlas shared by this scene's screen views.</summary>
internal sealed class ScreenText : IDisposable
{
    private readonly World _world;
    internal Font Font { get; }
    internal Entity AtlasSource { get; }

    internal ScreenText(World world)
    {
        _world = world;
        using Stream stream = typeof(ScreenText).Assembly.GetManifestResourceStream(
            "DefaultGame.Fonts.Example.otf")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        Font = Font.Load(bytes.ToArray());
        AtlasSource = world.CreateEntity(new TextAtlas(new GlyphAtlas()));
    }

    public void Dispose()
    {
        _world.DestroyEntity(AtlasSource);
        Font.Dispose();
    }
}
