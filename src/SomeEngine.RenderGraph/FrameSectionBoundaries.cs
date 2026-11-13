namespace SomeEngine.RenderGraph;

internal sealed partial class RenderGraphFrameState
{
    private readonly record struct SectionRestore(GraphIdentity Resource, BufferRange BufferRange,
        TextureSubresourceRange TextureRange, GraphScheduleResourceState State);

    private void PrepareFrameSectionBoundaries()
    {
        FrameResourceAccess[] accesses = _dynamicAccesses.ToArray();
        var sections = accesses.Where(access => access.Pass.ScopeId == Identity && IsPassEnabled(access.Pass) &&
                access.Target.ScopeId == Graph.Identity && access.TargetKind is GraphAccessTargetKind.Buffer or GraphAccessTargetKind.Texture)
            .GroupBy(access => (_dynamicPasses[access.Pass.Index].Region, _dynamicPasses[access.Pass.Index].SectionPosition));
        foreach (var section in sections)
        {
            var restores = new Dictionary<Queue, List<SectionRestore>>(ReferenceEqualityComparer.Instance);
            foreach (FrameResourceAccess access in section)
            {
                bool persistent = access.TargetKind == GraphAccessTargetKind.Buffer
                    ? StructureIndex.Structure.Buffers.Get(access.Target).Lifetime == RenderGraphResourceLifetime.Persistent
                    : StructureIndex.Structure.Textures.Get(access.Target).Lifetime == RenderGraphResourceLifetime.Persistent;
                if (!persistent) continue;
                GraphScheduleResourceState? state = Graph.Schedule.Boundary(section.Key.Region, section.Key.SectionPosition,
                        resource => ResolveBoundaryResource(resource) == access.Target,
                        pass => IsPassEnabled(pass) && HasBufferUploads(StructureIndex.Structure.Passes.Get(pass).CallbackStorage))
                    .Select(value => (GraphScheduleResourceState?)value).FirstOrDefault();
                if (state is not { } expected) continue;
                if (!restores.TryGetValue(expected.Queue, out List<SectionRestore>? values)) restores.Add(expected.Queue, values = []);
                values.Add(new(access.Target, access.BufferRange, access.TextureRange, expected));
            }
            foreach (var restore in restores)
            {
                SectionRestore[] data = restore.Value.Distinct().ToArray();
                var queue = PassQueueSelection.OnQueue(restore.Key);
                var options = new PassOptions(Culling: PassCullingMode.NeverCull);
                GraphPassId pass = restore.Key.Type switch
                {
                    QueueType.Copy => AddCopyPass("Restore frame section resources", queue, data, options, DeclareRestore,
                        static (ref CopyPassCommandScope _, in SectionRestore[] _) => { }, new(section.Key.Region), section.Key.SectionPosition),
                    QueueType.Compute => AddComputePass("Restore frame section resources", queue, data, options, DeclareRestore,
                        static (ref ComputePassCommandScope _, in SectionRestore[] _) => { }, new(section.Key.Region), section.Key.SectionPosition),
                    _ => AddGeneralPass("Restore frame section resources", queue, data, options, DeclareRestore,
                        static (ref GeneralPassCommandScope _, in SectionRestore[] _) => { }, new(section.Key.Region), section.Key.SectionPosition),
                };
                FramePass row = _dynamicPasses[pass.Value.Index];
                row.SectionBoundary = true;
                _dynamicPasses[pass.Value.Index] = row;
            }
        }
    }

    private GraphIdentity ResolveBoundaryResource(GraphScheduleResource resource)
    {
        if (resource.Resource.IsValid) return resource.Resource;
        if (resource.Input is GraphBufferInput buffer && _bufferInputs.TryGetValue(buffer, out var bufferValue) && bufferValue.Frame == Identity)
            return bufferValue.Value.Buffer.Value;
        if (resource.Input is GraphTextureInput texture && _textureInputs.TryGetValue(texture, out var textureValue) && textureValue.Frame == Identity)
            return textureValue.Value.Value;
        if (resource.Input is GraphParameterInput parameter)
        {
            ParameterInputValue? value = _parameterInputs.TryGetValue(parameter, out var current) && current.Frame == Identity
                ? current.Value : parameter.PersistentValue;
            if (value is { } binding) return resource.Secondary ? binding.Secondary : binding.Resource;
        }
        return default;
    }

    private static void DeclareRestore(ref PassDefinition pass, ref SectionRestore[] restores)
    {
        foreach (SectionRestore restore in restores)
        {
            GraphScheduleResourceState state = restore.State;
            if (restore.Resource.Domain == GraphIdentityDomain.Buffer)
            {
                if (state.Mode == GraphAccessMode.Read)
                    pass.Read(new GraphBufferId(restore.Resource), restore.BufferRange, state.Sync, state.Access, contentScope: state.ContentScope);
                else pass.Write(new GraphBufferId(restore.Resource), restore.BufferRange, state.Sync, state.Access,
                    WriteCoverage.Partial, contentScope: state.ContentScope);
            }
            else
            {
                if (state.Mode == GraphAccessMode.Read)
                    pass.Read(new GraphTextureId(restore.Resource), restore.TextureRange, state.Sync, state.Access, state.Layout);
                else pass.Write(new GraphTextureId(restore.Resource), restore.TextureRange, state.Sync, state.Access,
                    state.Layout, WriteCoverage.Partial);
            }
        }
    }
}
