using SomeEngine.Render.Shadows;
using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;
using Texture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>
/// Owns persistent virtual-shadow page tables, physical-page allocation state, atlas storage and
/// cross-frame readiness. Geometry pipelines consume imported frame resources but never own them.
/// </summary>
public sealed class VirtualShadowPages : IDisposable
{

    private readonly RenderPipelines _renderPipelines;
    public static ulong CalculatePageListBufferSize(uint capacity) => checked(4ul * (capacity + 1ul) * sizeof(uint));
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    public GraphBufferId PageTable { get; private set; }
    public GraphBufferId PageAssignments { get; private set; }
    public GraphTextureId Atlas { get; private set; }
    public GraphBufferId PageMetadata { get; private set; }
    public GraphBufferId PageLists { get; private set; }
    private bool _pageTableInitialized;
    internal VirtualShadowPageTableRanges Ranges { get; private set; } = new();
    internal uint PageTableEntryCapacity { get; private set; }
    private bool _disposed;

    public VirtualShadowPages(
        IGraphicsBackend backend,
        Device device,
        RenderPipelines pipelines,
        VirtualShadowMapShaders shaders,
        VirtualShadowMapSettings settings)    {

        _renderPipelines = pipelines;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentNullException.ThrowIfNull(shaders);
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        settings.Validate();
        LinkedComputePipeline? resetFrame = null;
        LinkedComputePipeline? resetReceiverMasks = null;
        LinkedComputePipeline? markCoarsePages = null;
        LinkedComputePipeline? allocatePages = null;
        LinkedComputePipeline? clearPages = null;
        LinkedComputePipeline? initializeState = null;
        LinkedComputePipeline? buildPagePool = null;
        LinkedComputePipeline? compactViews = null;
        LinkedComputePipeline? initializePageHiZ = null;
        LinkedComputePipeline? reducePageHiZ = null;
        LinkedComputePipeline? completePageDepth = null;
        LinkedComputePipeline? dynamicResolution = null;
        try
        {
            resetReceiverMasks = LinkedComputePipeline.Create(_renderPipelines, shaders.ResetReceiverMasksShader, shaders.ResetReceiverMasks, "Virtual shadow receiver mask reset");
            resetFrame = LinkedComputePipeline.Create(_renderPipelines, shaders.ResetFrameShader, shaders.ResetFrame, "Virtual shadow frame reset");
            markCoarsePages = LinkedComputePipeline.Create(_renderPipelines, shaders.MarkCoarsePagesShader, shaders.MarkCoarsePages, "Virtual shadow coarse page marking");
            allocatePages = LinkedComputePipeline.Create(_renderPipelines, shaders.AllocatePagesShader, shaders.AllocatePages, "Virtual shadow physical-page allocation");
            clearPages = LinkedComputePipeline.Create(_renderPipelines, shaders.ClearPagesShader, shaders.ClearPages, "Virtual shadow active-page clear");
            initializeState = LinkedComputePipeline.Create(_renderPipelines, shaders.InitializeStateShader, shaders.InitializeState, "Virtual shadow state initialization");
            buildPagePool = LinkedComputePipeline.Create(_renderPipelines, shaders.BuildPagePoolShader, shaders.BuildPagePool, "Virtual shadow page-pool build");
            compactViews = LinkedComputePipeline.Create(_renderPipelines, shaders.CompactViewsShader, shaders.CompactViews, "Virtual shadow active-view compaction");
            initializePageHiZ = LinkedComputePipeline.Create(_renderPipelines, shaders.InitializePageHiZShader, shaders.InitializePageHiZ, "Virtual shadow physical-page HiZ initialization");
            completePageDepth = LinkedComputePipeline.Create(_renderPipelines, shaders.CompletePageDepthShader, shaders.CompletePageDepth, "Complete virtual shadow page depth");
            reducePageHiZ = LinkedComputePipeline.Create(_renderPipelines, shaders.ReducePageHiZShader, shaders.ReducePageHiZ, "Virtual shadow physical-page HiZ reduction");
            dynamicResolution = LinkedComputePipeline.Create(_renderPipelines, shaders.DynamicResolutionShader, shaders.DynamicResolution, "Virtual shadow dynamic resolution feedback");

        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            if (clearPages is not null) TryDispose(clearPages, ref cleanupFailures);
            if (compactViews is not null) TryDispose(compactViews, ref cleanupFailures);
            if (completePageDepth is not null) TryDispose(completePageDepth, ref cleanupFailures);
            if (reducePageHiZ is not null) TryDispose(reducePageHiZ, ref cleanupFailures);
            if (dynamicResolution is not null) TryDispose(dynamicResolution, ref cleanupFailures);
            if (initializePageHiZ is not null) TryDispose(initializePageHiZ, ref cleanupFailures);
            if (buildPagePool is not null) TryDispose(buildPagePool, ref cleanupFailures);
            if (initializeState is not null) TryDispose(initializeState, ref cleanupFailures);
            if (allocatePages is not null) TryDispose(allocatePages, ref cleanupFailures);
            if (markCoarsePages is not null) TryDispose(markCoarsePages, ref cleanupFailures);
            if (resetFrame is not null) TryDispose(resetFrame, ref cleanupFailures);
            if (resetReceiverMasks is not null) TryDispose(resetReceiverMasks, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    "Virtual-shadow construction failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }

        FrameResetPipeline = resetFrame;
        ReceiverMaskResetPipeline = resetReceiverMasks;
        CoarsePageMarkPipeline = markCoarsePages;
        PageAllocatePipeline = allocatePages;
        PageClearPipeline = clearPages;
        StateInitializePipeline = initializeState;
        PagePoolPipeline = buildPagePool;
        ViewCompactPipeline = compactViews;
        PageHiZInitializePipeline = initializePageHiZ;
        PageHiZReducePipeline = reducePageHiZ;
        PageDepthCompletePipeline = completePageDepth;
        DynamicResolutionPipeline = dynamicResolution;
    }

    public void Install(ref RenderGraphEdit edit)
    {
        BufferUsages uses = BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
            BufferUsages.CopySource | BufferUsages.CopyDestination;
        uint entries = checked(Settings.MaxShadowLights * Settings.MaximumViewsPerLight * Settings.PageTableEntriesPerView);
        PageTableEntryCapacity = entries;
        PageTable = edit.CreatePersistentBuffer(new BufferDesc(Math.Max(1ul, entries) * VirtualShadowView.PageTableEntrySizeInBytes,
            uses, "Virtual shadow page table"));
        PageAssignments = edit.CreatePersistentBuffer(new BufferDesc((ulong)Settings.MaxPages * sizeof(uint),
            uses, "Virtual shadow page owners"));
        PageMetadata = edit.CreatePersistentBuffer(new BufferDesc((ulong)Settings.MaxPages * VirtualShadowPageMetadata.SizeInBytes,
            uses, "Virtual shadow page metadata"));
        PageLists = edit.CreatePersistentBuffer(new BufferDesc(CalculatePageListBufferSize(Settings.MaxPages),
            uses, "Virtual shadow page lists"));
        Atlas = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D, Settings.AtlasSize,
            Settings.AtlasHeight, 1, 1, 2, 1, Format.R32UInt,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
            label: "Virtual shadow atlas"));
    }

    public void SetPageTableCapacity(ref RenderGraphEdit edit, uint entries)
    {
        edit.SetBufferDescription(PageTable, new BufferDesc(Math.Max(1ul, entries) * VirtualShadowView.PageTableEntrySizeInBytes,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
            "Virtual shadow page table"));
    }

    public VirtualShadowMapSettings Settings { get; private set; }
    internal void SetSettings(ref RenderGraphEdit edit, VirtualShadowMapSettings settings, uint entries)
    {
        SetPageTableCapacity(ref edit, entries);
        BufferUsages uses = BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination;
        edit.SetBufferDescription(PageAssignments, new BufferDesc((ulong)settings.MaxPages * sizeof(uint), uses, "Virtual shadow page owners"));
        edit.SetBufferDescription(PageMetadata, new BufferDesc((ulong)settings.MaxPages * VirtualShadowPageMetadata.SizeInBytes, uses, "Virtual shadow page metadata"));
        edit.SetBufferDescription(PageLists, new BufferDesc(CalculatePageListBufferSize(settings.MaxPages), uses, "Virtual shadow page lists"));
        edit.SetTextureDescription(Atlas, new TextureDesc(TextureDimension.Texture2D, settings.AtlasSize,
            settings.AtlasHeight, 1, 1, 2, 1, Format.R32UInt,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment, label: "Virtual shadow atlas"));
    }

    internal void AcceptPageTableCapacity(uint entries)
    {
        if (entries != PageTableEntryCapacity) _pageTableInitialized = false;
        PageTableEntryCapacity = entries;
    }

    internal void InvalidateResources() => _pageTableInitialized = false;

    internal void AcceptSettings(VirtualShadowMapSettings settings, uint entries)
    {
        bool addressChanged = Settings.VirtualPagesPerView != settings.VirtualPagesPerView ||
            Settings.PageTableEntriesPerView != settings.PageTableEntriesPerView;
        bool storageChanged = addressChanged || Settings.AtlasSize != settings.AtlasSize ||
            Settings.PageSize != settings.PageSize || Settings.MaxPages != settings.MaxPages ||
            Settings.DynamicHiZ != settings.DynamicHiZ;
        if (addressChanged) Ranges = new();
        AcceptPageTableCapacity(entries);
        Settings = settings;
        if (storageChanged) _pageTableInitialized = false;
    }

    public LinkedComputePipeline FrameResetPipeline { get; }
    public LinkedComputePipeline ReceiverMaskResetPipeline { get; }
    public LinkedComputePipeline CoarsePageMarkPipeline { get; }
    public LinkedComputePipeline PageAllocatePipeline { get; }
    public LinkedComputePipeline PageClearPipeline { get; }
    public LinkedComputePipeline StateInitializePipeline { get; }
    public LinkedComputePipeline PagePoolPipeline { get; }
    public LinkedComputePipeline ViewCompactPipeline { get; }
    public LinkedComputePipeline PageHiZInitializePipeline { get; }
    public LinkedComputePipeline PageHiZReducePipeline { get; }
    public LinkedComputePipeline PageDepthCompletePipeline { get; }
    public LinkedComputePipeline DynamicResolutionPipeline { get; }
    public bool RequiresInitialization => !_pageTableInitialized;
    public VirtualShadowMapFrameResources AcquireFrameResources() => new(!_pageTableInitialized);

    public void Submit() => _pageTableInitialized = true;

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        TryDispose(PageClearPipeline, ref failures);
        TryDispose(PageDepthCompletePipeline, ref failures);
        TryDispose(PageHiZReducePipeline, ref failures);
        TryDispose(DynamicResolutionPipeline, ref failures);
        TryDispose(PageHiZInitializePipeline, ref failures);
        TryDispose(ViewCompactPipeline, ref failures);
        TryDispose(PagePoolPipeline, ref failures);
        TryDispose(StateInitializePipeline, ref failures);
        TryDispose(PageAllocatePipeline, ref failures);
        TryDispose(CoarsePageMarkPipeline, ref failures);
        TryDispose(FrameResetPipeline, ref failures);
        TryDispose(ReceiverMaskResetPipeline, ref failures);
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}


public readonly record struct VirtualShadowMapFrameResources(bool RequiresInitialization);
