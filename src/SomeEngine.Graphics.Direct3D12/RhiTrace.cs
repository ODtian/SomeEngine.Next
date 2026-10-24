namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class RhiTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite ReuseNativeCommands = new("rhi", nameof(ReuseNativeCommands), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite EncodeNativeCommands = new("rhi", nameof(EncodeNativeCommands), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite CreateWaitEvent = new("native", nameof(CreateWaitEvent), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite CloseWaitEvent = new("native", nameof(CloseWaitEvent), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite CpuFenceWait = new("wait", nameof(CpuFenceWait));
    internal static global::SomeEngine.Diagnostics.EngineTrace.TraceScope BeginResourceCreation()
        => global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiResourceCreation);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiBarriers = new("rhi", nameof(RhiBarriers), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiBindings = new("rhi", nameof(RhiBindings), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiCommandsAndOther = new("rhi", nameof(RhiCommandsAndOther), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiCommandLifecycle = new("rhi", nameof(RhiCommandLifecycle), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiSubmit = new("rhi", nameof(RhiSubmit), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiSubmitPrepareSubmissionWaits = new("rhi", nameof(RhiSubmitPrepareSubmissionWaits), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiSubmitClaimCommandPayloads = new("rhi", nameof(RhiSubmitClaimCommandPayloads), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiSubmitCollectReferencedSwapchainImages = new("rhi", nameof(RhiSubmitCollectReferencedSwapchainImages), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiSubmitValidateSwapchainImages = new("rhi", nameof(RhiSubmitValidateSwapchainImages), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiSubmitClaimSwapchainImages = new("rhi", nameof(RhiSubmitClaimSwapchainImages), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiSubmitExecuteSubmissionUnderGate = new("rhi", nameof(RhiSubmitExecuteSubmissionUnderGate), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiWaitAndCollect = new("rhi", nameof(RhiWaitAndCollect), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiResourceCreation = new("rhi", nameof(RhiResourceCreation), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite CreatePipeline = new("rhi", nameof(CreatePipeline));
    internal static readonly global::SomeEngine.Diagnostics.TraceSite RhiPresentation = new("rhi", nameof(RhiPresentation), detail: true);
}
