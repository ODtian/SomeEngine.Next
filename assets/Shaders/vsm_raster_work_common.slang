#ifndef VSM_RASTER_WORK_COMMON_SLANG
#define VSM_RASTER_WORK_COMMON_SLANG

struct VirtualShadowRasterWorkUniforms
{
    uint ViewCount;
    uint MipLevelCount;
    uint PageTableEntriesPerView;
    uint HierarchyWordsPerView;
    uint ActivePageCapacity;
    uint PageSize;
    uint PageHiZMipCount;
    uint PhysicalPagesPerRow;
    uint InstanceGroupCount;
    uint HasPreviousHistory;
    uint WorkFeedbackEnabled;
    uint StaticHiZLayerOffset;
};

#endif
