using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Render.Features.GI;

public sealed partial class ScreenProbeFeature
{
    public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
    {
        binder = name switch
        {
            "GiCapture" => BindCapture,
            "GiSurfaceNormal" => BindSurfaceNormal,
            "GiSurfaceDiffuse" => BindSurfaceDiffuse,
            "GiSurfaceRadiance" => BindSurfaceRadiance,
            "GiSurfaceInstance" => BindSurfaceInstance,
            _ => null!,
        };
        return binder is not null;
    }

    private void BindCapture(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) => pass.Bind(_captureSettings, sync);
    private void BindSurfaceNormal(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) => BindSurface(ref edit, ref pass, _normal, Format.R16G16B16A16Float, sync);
    private void BindSurfaceDiffuse(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) => BindSurface(ref edit, ref pass, _diffuse, Format.R8G8B8A8UNorm, sync);
    private void BindSurfaceRadiance(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) => BindSurface(ref edit, ref pass, _reflectedRadiance, Format.R11G11B10Float, sync);
    private void BindSurfaceInstance(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) => BindSurface(ref edit, ref pass, _surfaceInstance, Format.R32UInt, sync);


    private void BindBuffer(ref RenderGraphEdit edit, ref PassDefinition pass, string name, ResourceBindingType type,
        PipelineSync sync, GraphAccessMode access = GraphAccessMode.ReadWrite, WriteCoverage coverage = WriteCoverage.Partial,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        if (!Enabled) { pass.Bind(GraphParameterResourceBinding.Null(type)); return; }
        BufferData buffer = _buffers[name];
        if (type == ResourceBindingType.BufferSrv) pass.Bind(edit.CreateBufferSrv(buffer.Id, structureStride: buffer.Stride), sync);
        else pass.Bind(edit.CreateBufferUav(buffer.Id, structureStride: buffer.Stride), sync, access, coverage,
            unorderedAccessHazards: unorderedAccessHazards);
    }

    private void BindSurface(ref RenderGraphEdit edit, ref PassDefinition pass, GraphTextureId texture, Format format, PipelineSync sync)
    {
        if (Enabled)
        {
            edit.OrderAfter(pass.Id, ClearSurfacesPass);
            pass.Bind(edit.CreateTextureUav(texture, format: format, dimension: TextureViewDimension.Texture2DArray),
                sync, GraphAccessMode.Write, WriteCoverage.Partial,
                unorderedAccessHazards: UnorderedAccessHazardMode.DisjointWrites);
        }
        else pass.Bind(GraphParameterResourceBinding.Null(ResourceBindingType.TextureUav));
    }

    private void BindSceneResource(ref RenderGraphEdit edit, ref PassDefinition pass, string name, ResourceBindingType type, PipelineSync sync, int stage = 0)
    {
        if (_buffers.ContainsKey(name)) { BindBuffer(ref edit, ref pass, name, type, sync); return; }
        if (BindEmissiveResource(ref edit, ref pass, name, type, sync)) return;
        switch (name)
        {
            case "GiSettings": pass.Bind(_screenSettings, sync); return;
            case "GiCache": pass.Bind(_cacheSettings, sync); return;
            case "GiScene": pass.Bind(_sceneSettings[stage], sync); return;
            case "GiMeshFields": pass.Bind(edit.CreateBufferSrv(_scene!.MeshFields), sync); return;
            case "GiMeshFieldsIndex": pass.Bind(edit.CreateBufferSrv(_scene!.MeshFieldsIndex, structureStride: 8), sync); return;
            case "GiHierarchy": _surfaces.Geometry.BindBvh(edit, ref pass, sync); return;
            case "PageHeap": _surfaces.Geometry.BindPageHeap(edit, ref pass, sync); return;
            case "GiInstanceBounds": pass.Bind(edit.CreateBufferSrv(_surfaces.Geometry.InstanceWorldBounds, structureStride: 16), sync); return;
            case "GiInstanceGroups":
                if (_surfaces.Geometry.UseInstanceGroups) pass.Bind(edit.CreateBufferSrv(_surfaces.Geometry.InstanceGroups, structureStride: 16), sync, BufferContentScope.CountBoundedPrefix);
                else pass.Bind(GraphParameterResourceBinding.Null(type));
                return;
            case "GiInstanceOrder":
                if (_surfaces.Geometry.UseInstanceGroups) pass.Bind(_surfaces.Geometry.InstanceOrder, sync);
                else pass.Bind(GraphParameterResourceBinding.Null(type));
                return;
            case "GiPageRequests": pass.Bind(edit.CreateBufferUav(_surfaces.Geometry.PageRequests), sync, GraphAccessMode.ReadWrite,
                unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable); return;
            case "InstanceData": pass.Bind(edit.CreateBufferSrv(_surfaces.Geometry.InstanceData), sync); return;
            case "InstanceProperties": pass.Bind(edit.CreateBufferCbv(_surfaces.Geometry.InstanceProperties, _surfaces.Geometry.InstancePropertiesRange), sync); return;
            case "GiViews": pass.Bind(edit.CreateBufferSrv(_surfaces.Geometry.PackedViews, structureStride: _surfaces.Geometry.PackedViewStride), sync); return;
            case "GiDepth":
                pass.Bind(edit.CreateTextureSrv(_surfaces.Depth, new TextureSubresourceRange(0, 1, 0, _views, TextureAspects.Depth),
                    Format.D32Float, TextureViewDimension.Texture2DArray), sync); return;
            case "GiVisibility": BindTexture(ref edit, ref pass, _surfaces.Visibility, Format.R32UInt, sync); return;
            case "GiMotion": BindTexture(ref edit, ref pass, _surfaces.MotionVectors, Format.R16G16Float, sync); return;
            case "GiNormal": BindTexture(ref edit, ref pass, _normal, Format.R16G16B16A16Float, sync); return;
            case "GiDiffuse": BindTexture(ref edit, ref pass, _diffuse, Format.R8G8B8A8UNorm, sync); return;
            case "GiReflectedRadiance": BindTexture(ref edit, ref pass, _reflectedRadiance, Format.R11G11B10Float, sync); return;
            case "GiSurfaceInstances": BindTexture(ref edit, ref pass, _surfaceInstance, Format.R32UInt, sync); return;
            case "GiColor":
                pass.Bind(edit.CreateTextureUav(_surfaces.MaterialColor, format: Format.R16G16B16A16Float,
                    dimension: TextureViewDimension.Texture2DArray), sync, GraphAccessMode.ReadWrite); return;
        }
        if (_shared is not null && _shared.TryResolve(name, type, out GraphResourceBinder binder)) { binder(ref edit, ref pass, sync); return; }
        throw new InvalidDataException($"GI resource '{name}' has no binding.");
    }

    private static void BindTexture(ref RenderGraphEdit edit, ref PassDefinition pass, GraphTextureId texture, Format format, PipelineSync sync) =>
        pass.Bind(edit.CreateTextureSrv(texture, format: format, dimension: TextureViewDimension.Texture2DArray), sync);

    private sealed class MaterialResources(ScreenProbeFeature feature, GraphBufferId constant,
        string? outputBufferName = null) : IGraphResourceProvider
    {
        public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
        {
            if (name == "GiMaterialWork") binder = BindIndex;
            else binder = (ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
            {
                if (name == "GiClosestDistances")
                    feature.BindBuffer(ref edit, ref pass, name, type, sync,
                        unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
                else if (name == outputBufferName || outputBufferName == "GiCandidateHits" && name == "GiCandidates")
                    feature.BindBuffer(ref edit, ref pass, name, type, sync, GraphAccessMode.Write,
                        unorderedAccessHazards: UnorderedAccessHazardMode.DisjointWrites);
                else if (name is "GiMaterialWorkRanges" or "GiMaterialWorkIndices" or "GiMeshQueries" or "GiCandidates" or "GiRays" or
                    "GiCandidateHits" or "GiSurfaceSamples")
                    feature.BindBuffer(ref edit, ref pass, name, type, sync, GraphAccessMode.Read);
                else feature.BindSceneResource(ref edit, ref pass, name, type, sync);
            };
            return true;
        }
        private void BindIndex(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync) =>
            pass.Bind(edit.CreateBufferCbv(constant, BufferRange.Whole), sync);
    }
}
