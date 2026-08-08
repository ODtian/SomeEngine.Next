#ifndef SOMEENGINE_RENDER_INSTANCE_LAYOUT_INCLUDED
#define SOMEENGINE_RENDER_INSTANCE_LAYOUT_INCLUDED

// Exact dense metadata layout for the currently cooked Cluster shader variant. Shader-cook
// integration must emit one such document for each pipeline/material composition.
static const uint RENDER_INSTANCE_METADATA_WORD_COUNT = 5u;
static const uint RENDER_INSTANCE_METADATA_VECTOR_COUNT =
    (RENDER_INSTANCE_METADATA_WORD_COUNT + 3u) / 4u;

struct RenderInstancePropertyMetadata
{
    // Constant-buffer scalar arrays have a 16-byte element stride. Packing into uint4
    // vectors preserves the CPU-side dense uint-word ABI.
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
static const uint CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD = 2u;
static const uint RENDER_CURRENT_TRANSFORM_METADATA_WORD = 3u;
static const uint RENDER_PREVIOUS_TRANSFORM_METADATA_WORD = 4u;

#endif