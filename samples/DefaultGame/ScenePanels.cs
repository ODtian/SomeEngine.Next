using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements.Render;
using SomeEngine.Render.Systems;
using SomeEngine.Runtime;

namespace DefaultGame;

/// <summary>The sample's screen panel, HUD and glyph extraction.</summary>
internal sealed class ScenePanels : ISystem<ImmediateSystemContext>, IRenderExtractionSystem
{
    private readonly ApplicationFrame _frame;
    private readonly bool _changeNumbers;
    private readonly ScreenText? _text;
    private readonly SurfacePanelExample? _panel;
    private readonly HudOverlay? _hud;
    private readonly IRenderExtractionSystem? _glyphs;
    private bool _disposed;

    private ScenePanels(ApplicationFrame frame, SampleSceneOptions options)
    {
        _frame = frame;
        _changeNumbers = options.HudNumbersChange;
        try
        {
            if (options.SurfacePanel || options.HudOverlay)
            {
                _text = new ScreenText(frame.World);
                if (options.SurfacePanel)
                    _panel = new SurfacePanelExample(frame.World, frame.Width, frame.Height, _text);
                if (options.HudOverlay)
                    _hud = new HudOverlay(frame.World, frame.Width, frame.Height, _text);
                _glyphs = new GlyphRenderExtractor(_text.AtlasSource);
                // Extraction owns these resources even if initialization fails before
                // any game-system update, and outlives every Shutdown callback.
                frame.Extraction.Add(this);
                frame.Systems.AddGameSystem(this);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static void Install(ApplicationScene application, Func<SampleSceneOptions> readOptions)
    {
        SampleSceneOptions options = readOptions();
        if (options.SurfacePanel || options.HudOverlay) application.InterfaceEnabled = true;
        Action<ApplicationFrame>? initialize = application.Initialize;
        application.Initialize = frame =>
        {
            SampleSceneOptions current = readOptions();
            if (current.SurfacePanel || current.HudOverlay) _ = new ScenePanels(frame, current);
            initialize?.Invoke(frame);
        };
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        // Both DefaultGame run paths already update the game synchronously.
        // Resize and update the displayed frame even when simulation time is paused.
        if (_frame.Width > 0 && _frame.Height > 0)
        {
            _panel?.Resize(_frame.Width, _frame.Height);
            _hud?.Resize(_frame.Width, _frame.Height);
        }
        if (_changeNumbers) _hud?.Update(_frame.Index);
    }

    void IRenderExtractionSystem.DeclareReads(RenderExtractionQuery query) => _glyphs!.DeclareReads(query);
    void IRenderExtractionSystem.Reset() => _glyphs!.Reset();
    void IRenderExtractionSystem.CollectResources(scoped QueryCursor source, RenderExtractionResources resources) =>
        _glyphs!.CollectResources(source, resources);
    void IRenderExtractionSystem.Collect(QueryChunkView chunk) => _glyphs!.Collect(chunk);
    void IRenderExtractionSystem.Apply(RenderExtractionContext context) => _glyphs!.Apply(context);
    bool IRenderExtractionSystem.CollectChanges(QueryChunkView chunk, RenderExtractionContext context) =>
        _glyphs!.CollectChanges(chunk, context);
    bool IRenderExtractionSystem.CompleteSourceCollection(World sourceWorld, bool fullCollection) =>
        _glyphs!.CompleteSourceCollection(sourceWorld, fullCollection);
    bool IRenderExtractionSystem.CompleteCollection(RenderExtractionContext context, bool fullCollection) =>
        _glyphs!.CompleteCollection(context, fullCollection);
    void IRenderExtractionSystem.ApplyChanges(RenderExtractionContext context) => _glyphs!.ApplyChanges(context);
    bool IRenderExtractionSystem.CanApplyChangesDirect => _glyphs!.CanApplyChangesDirect;
    void IRenderExtractionSystem.ValidateChanges(RenderExtractionContext context) => _glyphs!.ValidateChanges(context);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _glyphs?.Dispose(); }
        finally
        {
            try { _hud?.Dispose(); }
            finally
            {
                try { _panel?.Dispose(); }
                finally { _text?.Dispose(); }
            }
        }
    }
}
