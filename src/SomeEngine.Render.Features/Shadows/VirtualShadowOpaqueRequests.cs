using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Lighting;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>Requests shadow pages from primary visibility and supplies the GPU light-to-view indices.</summary>
internal sealed class VirtualShadowOpaqueRequests(VirtualShadowFeature feature, Shader shader, EntryPointReflection entry)
{
    private Shader _shader = shader;
    private readonly string _entryPoint = entry.Name;
    private RequestInputs _inputs;
    private VirtualShadowVisibilityResources _resources;

    internal GraphPassId CompletePass => _resources.CompletePass;

    internal void Reconcile(RenderGeometryResources geometry, GraphTextureId visibility,
        GraphTextureId depth, uint width, uint height, uint viewCount)
    {
        var inputs = new RequestInputs(geometry, visibility, depth, width, height, viewCount);
        if (_resources.CompletePass != default && _inputs == inputs) return;
        Install(in inputs, _shader);
    }

    private void Install(in RequestInputs inputs, Shader source)
    {
        RenderGraphEdit edit = feature._renderGraph.BeginEdit();
        try
        {
            if (_resources.CompletePass != default)
                VirtualShadowVisibilityPasses.Remove(ref edit, in _resources);
            var bindings = new RequestResources(feature);
            VirtualShadowVisibilityResources resources = VirtualShadowVisibilityPasses.Add(ref edit,
                feature._renderPipelines, source, inputs.Geometry, inputs.Visibility, inputs.Depth, bindings,
                inputs.Width, inputs.Height, inputs.ViewCount, feature._requests.ShadowPageRequests, _entryPoint);
            edit.Commit();
            _inputs = inputs;
            _resources = resources;
            _shader = source;
        }
        finally { edit.Dispose(); }
    }

    internal void Record(ref RenderGraphFrame frame, RenderLightSet lights,
        VirtualShadowViews shadowViews)
    {
        int lightCount = Math.Max(1, lights.Directional.Count + lights.Points.Count + lights.Spots.Count);
        GraphBufferSlice indices = frame.Upload(checked(lightCount * sizeof(uint)), BufferUsages.ShaderRead,
            shadowViews, static (bytes, views) => VirtualShadowLightViews.WriteIndices(
                views.Views, MemoryMarshal.Cast<byte, uint>(bytes)), "Shadow light view indices");
        frame.SetBufferInput(feature._frameLightViewIndices, indices);
        VirtualShadowVisibilityPasses.Record(ref frame, in _resources,
            _inputs.Width, _inputs.Height, _inputs.ViewCount);
        frame.SetPassEnabled(_resources.CompletePass, !shadowViews.Views.IsEmpty);
    }

    internal void ReplaceShader(Shader previous, Shader next)
    {
        if (!ReferenceEquals(_shader, previous)) return;
        if (_resources.CompletePass == default)
        {
            _shader = next;
            return;
        }
        Install(in _inputs, next);
    }

    private readonly record struct RequestInputs(RenderGeometryResources Geometry, GraphTextureId Visibility,
        GraphTextureId Depth, uint Width, uint Height, uint ViewCount);

    private sealed class RequestResources(VirtualShadowFeature feature)
        : IGraphResourceProvider
    {
        public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
        {
            return feature.TryResolve(name, type, out binder) || feature._lighting.TryResolve(name, type, out binder);
        }
    }
}
