#ifndef SOMEENGINE_RENDER_INSTANCE_LAYOUT_INCLUDED
#define SOMEENGINE_RENDER_INSTANCE_LAYOUT_INCLUDED

// Dense metadata layout generated from the registered Cluster feature.
static const uint RENDER_INSTANCE_METADATA_WORD_COUNT = 9u;
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

static const uint CLUSTER_BOUNDS_EXPANSION_METADATA_WORD = 0u;
static const uint CLUSTER_BVH_ROOT_METADATA_WORD = 1u;
static const uint CLUSTER_DEFORM_BOUNDS_METADATA_WORD = 2u;
static const uint CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD = 3u;
static const uint RENDER_CURRENT_TRANSFORM_METADATA_WORD = 4u;
static const uint RENDER_PREVIOUS_TRANSFORM_METADATA_WORD = 5u;
static const uint RENDER_DEPTH_CACHE_CLASS_METADATA_WORD = 6u;
static const uint RENDER_FIRST_PERSON_METADATA_WORD = 7u;

static const uint SKIN_BINDING_METADATA_WORD = 8u;

#endif