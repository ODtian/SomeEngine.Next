using SomeEngine.Render.Features.GI;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using SomeEngine.Runtime;

namespace DefaultGame;

/// <summary>Chooses this application's GI assets and installs the independent rendering feature.</summary>
internal sealed class ExperimentalGi(Func<bool> enabled) : IApplicationGeometryFeature
{
    private ScreenProbeFeature? _screen;

    public IDisposable Install(ApplicationGeometryContext context)
    {
        bool active = enabled();
        Shader? shader = active
            ? context.LoadShaderAsync(context.Settings.GlobalIlluminationShader!.Value).AsTask().GetAwaiter().GetResult()
            : null;
        _screen = new ScreenProbeFeature(context.Backend, context.Device, context.Graph,
            context.Width, context.Height, 1, shader, active);
        context.AddResources(_screen);
        return _screen;
    }

    internal IApplicationColorFeature Connect(ApplicationRendering rendering)
    {
        RenderGraphEdit edit = rendering.Graph.BeginEdit();
        try { _screen!.Connect(ref edit, rendering.Geometry); edit.Commit(); }
        finally { edit.Dispose(); }
        return new ProbeFrame(_screen, rendering);
    }

    private sealed class ProbeFrame(ScreenProbeFeature screen, ApplicationRendering rendering) : IApplicationColorFeature
    {
        private GraphPassId _complete = screen.CompletePass;
        public GraphPassId CompletePass => _complete;
        public void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorReady)
        {
            if (screen.Enabled) graph.OrderAfter(screen.CompletePass, colorReady);
            else _complete = colorReady;
        }
        public GraphTextureId Record(ref RenderGraphFrame graph, GraphTextureId color, GraphTextureId depth)
        {
            screen.Record(ref graph, rendering.CameraCut);
            return color;
        }
        public void Dispose() { }
    }
}
