#ifndef SOMEENGINE_SKIN_BINDING_INCLUDED
#define SOMEENGINE_SKIN_BINDING_INCLUDED

uint2 LoadSkinBinding(uint instanceID)
{
    return RenderInstanceLoad<uint2>(InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, SKIN_BINDING_METADATA_WORD), instanceID, 8u);
}

#endif
