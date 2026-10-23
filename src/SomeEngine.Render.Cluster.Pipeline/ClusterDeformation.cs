using SomeEngine.Graphics;
using SomeEngine.Resources;
using SlangShaderSharp;

namespace SomeEngine.Render.Cluster.Pipeline;

internal enum ClusterDeformResource : byte { None, Builtin }

internal static class ClusterDeformResourceNames
{
    internal static bool IsRead(string name) => name is "VertexDeformCache" or "VertexDeformRegions";

    internal static bool Contains(string name) => name is
        "DeformUniforms" or "DeformDrawArgs" or "DeformReadOffset0" or "DeformReadOffset1" or
        "DeformReadOffset2" or "DeformRasterBins" or "DeformVisibleClusters" or "DeformCacheWrite" or
        "DeformRegionsWrite" or "DeformRequests" or "DeformPrefixOffsets" or "DeformPrefixGroups" or
        "DeformArguments" or "DeformAllocation" or "PageHeap" or "InstanceData" or "InstanceProperties" or
        "MaterialScalarRegion" or "MaterialScalarOffsets";

    internal static ClusterDeformResource Resolve(string name, ResourceBindingType type) =>
        Contains(name) ? ClusterDeformResource.Builtin : ClusterDeformResource.None;
}

internal sealed record ClusterDeformStage(
    ClusterComputePipeline Pipeline,
    ClusterRasterStageResources<ClusterDeformResource> Resources);

internal sealed record ClusterDeformation(ClusterDeformStage[] Stages)
{
    internal static readonly string[] Entries = [
        "ClusterDeformArguments", "ClusterDeformSize", "ClusterDeformPrefixLocal",
        "ClusterDeformPrefixGroups", "ClusterDeformOffsets", "ClusterDeformWrite"];

    internal static bool SameBindings(ClusterDeformation? left, ClusterDeformation? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        for (int index = 0; index < Entries.Length; index++)
            if (!left.Stages[index].Resources.MaterialBindings.AsSpan().SequenceEqual(
                    right.Stages[index].Resources.MaterialBindings)) return false;
        return true;
    }
}

internal sealed partial class ClusterRasterStages
{
    private ClusterDeformation? ResolveDeformation(
        Material material, string vertexEncoding, List<ClusterCommandBindingKey> bindings)
    {
        Shader shader = ClusterMaterialEntries.RequireShader(material);
        if (!ClusterMaterialEntries.HasEntry(shader, _device.Capabilities.CompileTarget,
                "ClusterDeformSize", SlangStage.Compute)) return null;

        var stages = new ClusterDeformStage[ClusterDeformation.Entries.Length];
        for (int index = 0; index < stages.Length; index++)
        {
            string entry = ClusterDeformation.Entries[index];
            ClusterComputePipeline pipeline = ResolveSoftwarePipeline(material, shader, entry, vertexEncoding);
            ClusterRasterStageResources<ClusterDeformResource> resources = ResolveRasterResources(
                material, pipeline.ParameterLayout, shader, entry, null,
                ClusterDeformResourceNames.Resolve, ClusterDeformResourceNames.Contains);
            bindings.Add(ClusterCommandBindingKey.Create(material, resources.Resources, resources.MaterialBindings));
            stages[index] = new(pipeline, resources);
        }
        return new(stages);
    }
}
