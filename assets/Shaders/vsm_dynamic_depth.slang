__exported import vsm_types;

bool VirtualShadowDynamicReceiverRefresh(VirtualShadowMapParameters view)
{
    return (view.Flags & VSM_VIEW_USE_RECEIVER_MASK) != 0u &&
        (view.LightKind != VSM_LIGHT_DIRECTIONAL ||
            (view.Flags & VSM_VIEW_DIRECTIONAL_DYNAMIC_DEPTH_CACHE) == 0u);
}
