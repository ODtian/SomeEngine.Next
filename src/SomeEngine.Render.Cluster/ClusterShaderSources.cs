using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Cluster;

public static class ClusterShaderSources
{
    /// <summary>Builds the Slang instance layout consumed by Cluster shader entries.</summary>
    public static string BuildInstanceLayout(RenderInstancePropertyLayout layout)
    {
        int boundsExpansion = layout.Resolve<float>(ClusterRenderFeature.BoundsExpansionKey)
            .Descriptor.MetadataWordOffset;
        int bvhRoot = layout.Resolve<uint>(ClusterRenderFeature.BvhRootKey)
            .Descriptor.MetadataWordOffset;
        int materialIndex = layout.Resolve<uint>(ClusterRenderFeature.MaterialSlotOffsetKey)
            .Descriptor.MetadataWordOffset;
        int currentTransform = layout.Resolve(RenderInstanceTransformProperties.CurrentTransform)
            .Descriptor.MetadataWordOffset;
        int previousTransform = layout.Resolve(RenderInstanceTransformProperties.PreviousTransform)
            .Descriptor.MetadataWordOffset;

        return $$"""
            #ifndef SOMEENGINE_RENDER_INSTANCE_LAYOUT_INCLUDED
            #define SOMEENGINE_RENDER_INSTANCE_LAYOUT_INCLUDED

            // Dense metadata layout generated from the registered Cluster feature.
            static const uint RENDER_INSTANCE_METADATA_WORD_COUNT = {{layout.MetadataWordCount}}u;
            static const uint RENDER_INSTANCE_METADATA_VECTOR_COUNT =
                (RENDER_INSTANCE_METADATA_WORD_COUNT + 3u) / 4u;

            struct RenderInstancePropertyMetadata
            {
                uint4 PackedWords[RENDER_INSTANCE_METADATA_VECTOR_COUNT];
            };

            uint LoadRenderInstanceMetadata(
                RenderInstancePropertyMetadata metadata,
                uint wordIndex)
            {
                return metadata.PackedWords[wordIndex >> 2u][wordIndex & 3u];
            }

            static const uint CLUSTER_BOUNDS_EXPANSION_METADATA_WORD = {{boundsExpansion}}u;
            static const uint CLUSTER_BVH_ROOT_METADATA_WORD = {{bvhRoot}}u;
            static const uint CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD = {{materialIndex}}u;
            static const uint RENDER_CURRENT_TRANSFORM_METADATA_WORD = {{currentTransform}}u;
            static const uint RENDER_PREVIOUS_TRANSFORM_METADATA_WORD = {{previousTransform}}u;

            #endif
            """;
    }
}
