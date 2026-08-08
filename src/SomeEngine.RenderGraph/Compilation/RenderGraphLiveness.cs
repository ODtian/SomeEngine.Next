namespace SomeEngine.RenderGraph;

/// <summary>
/// Exact value liveness for one invocation. Producer state is tracked per resource range; hazard
/// ordering is intentionally built later because read/write serialization is not a liveness edge.
/// </summary>
internal static partial class RenderGraphCompiler
{
    internal static void AnalyzeLiveness(RenderGraph graph)
        => AnalyzeLivenessReference(
            graph,
            out _,
            out _,
            out _);

    internal static unsafe void AnalyzeLiveness(
        RenderGraph graph,
        out ArenaSlice<int> accessPassOrdinals,
        out ArenaSlice<int> resourceAccessOffsets,
        out ArenaSlice<int> resourceAccessOrdinals)
    {
        ArgumentNullException.ThrowIfNull(graph);
        int resourceCount = graph.ResourceCount;
        int passCount = graph.Passes.Length;
        int bufferCount = graph.Buffers.Length;
        PassData* canonicalPassRows =
            graph.Passes.DangerousContiguousPointer;
        PassInputData* canonicalPassInputs =
            graph.PassInputs.DangerousContiguousPointer;
        ResourceUnversionedData* canonicalBufferRows =
            graph.Buffers.DangerousContiguousPointer;
        ResourceUnversionedData* canonicalTextureRows =
            graph.Textures.DangerousContiguousPointer;
        int flagCount = checked(
            passCount +
            resourceCount +
            graph.BufferViewCount +
            graph.TextureViewCount +
            graph.AccelerationStructureCount);
        graph.LivenessFlags = graph.AllocateSlice<byte>(flagCount);
        graph.RetainingPasses =
            graph.AllocateSlice<int>(passCount, clear: false);
        graph.RetainingPasses.Span.Fill(-1);

        ArenaSlice<BufferBoundaryIndex> bufferBoundaries = BuildBufferBoundaries(
            graph,
            out accessPassOrdinals,
            out resourceAccessOffsets,
            out resourceAccessOrdinals);
        byte* liveFlags = graph.LivenessFlags.DangerousPointer;
        ArenaSlice<ProducerIndex> producerIndexes =
            graph.AllocateSlice<ProducerIndex>(resourceCount, clear: false);
        ArenaSlice<ContentMask> contents =
            graph.AllocateSlice<ContentMask>(resourceCount, clear: false);
        int cellStorageCount = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            if ((liveFlags[passCount + resource] &
                 RenderGraph.ResourceWrittenFlag) == 0)
            {
                continue;
            }
            int cellCount;
            if (resource < bufferCount)
            {
                cellCount = Math.Max(
                    0,
                    bufferBoundaries[resource].Count - 1);
            }
            else
            {
                GraphTextureDescription texture =
                    graph.GetTextureDescription(resource - bufferCount);
                int planes = GraphFormat.HasStencil(texture.Format) ? 2 : 1;
                cellCount = checked(
                    texture.MipLevels * texture.ArrayLayers * planes);
            }
            if (cellCount > 1)
                cellStorageCount = checked(cellStorageCount + cellCount);
        }
        ArenaSlice<int> producerCells =
            graph.AllocateSlice<int>(cellStorageCount, clear: false);
        producerCells.Span.Fill(-1);
        ArenaSlice<byte> contentCells =
            graph.AllocateSlice<byte>(cellStorageCount);
        int cellStorageOffset = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            if ((liveFlags[passCount + resource] &
                 RenderGraph.ResourceWrittenFlag) == 0)
            {
                continue;
            }
            int cellCount;
            bool initialized;
            if (resource < bufferCount)
            {
                ResourceUnversionedData buffer = canonicalBufferRows is not null
                    ? canonicalBufferRows[resource]
                    : graph.GetBufferByResourceOrdinal(resource);
                cellCount = Math.Max(
                    0,
                    bufferBoundaries[resource].Count - 1);
                initialized = buffer.ContentsInitialized ||
                    buffer.IsImported &&
                    buffer.ContentsAvailable;
            }
            else
            {
                int textureOrdinal = resource - bufferCount;
                ResourceUnversionedData textureRow = canonicalTextureRows is not null
                    ? canonicalTextureRows[textureOrdinal]
                    : graph.GetTextureByResourceOrdinal(resource);
                GraphTextureDescription textureDescription =
                    graph.GetTextureDescription(resource - bufferCount);
                int planes =
                    GraphFormat.HasStencil(textureDescription.Format)
                        ? 2
                        : 1;
                cellCount = checked(
                    textureDescription.MipLevels *
                    textureDescription.ArrayLayers *
                    planes);
                initialized = textureRow.IsImported &&
                    textureRow.ContentsAvailable;
            }
            ArenaSlice<int> resourceProducers = default;
            ArenaSlice<byte> resourceContents = default;
            if (cellCount > 1)
            {
                resourceProducers =
                    producerCells.Slice(cellStorageOffset, cellCount);
                resourceContents =
                    contentCells.Slice(cellStorageOffset, cellCount);
                cellStorageOffset = checked(cellStorageOffset + cellCount);
            }
            producerIndexes[resource] =
                new ProducerIndex(resourceProducers);
            contents[resource] =
                new ContentMask(resourceContents, initialized);
        }

        PassPredecessorTable prerequisites =
            new(graph, passCount);
        int rootCount = 0;
        for (int pass = 0; pass < passCount; pass++)
        {
            ref readonly PassData passRow = ref (
                canonicalPassRows is not null
                    ? ref canonicalPassRows[pass]
                    : ref graph.Passes[pass]);
            if ((passRow.Flags & PassFlags.NeverCull) != 0 &&
                graph.MarkPassRoot(pass))
            {
                rootCount++;
            }
        }

        for (int resource = 0; resource < resourceCount; resource++)
        {
            int firstAccess = resourceAccessOffsets[resource];
            int afterLastAccess = resourceAccessOffsets[resource + 1];
            if (firstAccess == afterLastAccess) continue;
            if ((liveFlags[passCount + resource] &
                 RenderGraph.ResourceWrittenFlag) == 0)
            {
                bool contentsAvailable;
                string resourceKind;
                if (resource < bufferCount)
                {
                    ResourceUnversionedData buffer = canonicalBufferRows is not null
                        ? canonicalBufferRows[resource]
                        : graph.GetBufferByResourceOrdinal(resource);
                    contentsAvailable = buffer.ContentsInitialized ||
                        buffer.IsImported &&
                        buffer.ContentsAvailable;
                    resourceKind = "buffer";
                }
                else
                {
                    int readTextureOrdinal = resource - bufferCount;
                    ResourceUnversionedData textureRow = canonicalTextureRows is not null
                        ? canonicalTextureRows[readTextureOrdinal]
                        : graph.GetTextureByResourceOrdinal(resource);
                    contentsAvailable = textureRow.IsImported &&
                        textureRow.ContentsAvailable;
                    resourceKind = "texture";
                }
                if (contentsAvailable) continue;
                int accessOrdinal = resourceAccessOrdinals[firstAccess];
                int pass = accessPassOrdinals[accessOrdinal];
                throw new InvalidOperationException(
                    $"Pass '{graph.GetPassName(pass)}' reads {resourceKind} content that has not been imported or fully produced.");
            }

            ref ProducerIndex producers = ref producerIndexes[resource];
            ref ContentMask resourceContents = ref contents[resource];
            if (resource < bufferCount)
            {
                int buffer = resource;
                bool bufferImported = canonicalBufferRows is not null
                    ? canonicalBufferRows[buffer].IsImported
                    : graph.GetBufferByResourceOrdinal(resource).IsImported;
                BufferBoundaryIndex boundaries = bufferBoundaries[buffer];
                for (int accessIndex = firstAccess;
                     accessIndex < afterLastAccess;
                     accessIndex++)
                {
                    int accessOrdinal =
                        resourceAccessOrdinals[accessIndex];
                    int pass = accessPassOrdinals[accessOrdinal];
                    ref readonly PassInputData access = ref (
                        canonicalPassInputs is not null
                            ? ref canonicalPassInputs[accessOrdinal]
                            : ref graph.PassInputs[accessOrdinal]);
                    bool readsPriorValue =
                        (access.Flags & GraphAccess.Read) != 0 ||
                        (access.Flags & GraphAccess.Discard) == 0;
                    bool producesValue =
                        (access.Flags & GraphAccess.Write) != 0;
                    ulong accessEnd =
                        checked(access.BufferRange.Offset +
                                access.BufferRange.Size);
                    int first;
                    int afterLast;
                    if (boundaries.Count == 2 &&
                        boundaries[0] == access.BufferRange.Offset &&
                        boundaries[1] == accessEnd)
                    {
                        first = 0;
                        afterLast = 1;
                    }
                    else
                    {
                        first = boundaries.Find(access.BufferRange.Offset);
                        afterLast = boundaries.Find(accessEnd);
                    }
                    if (first < 0 || afterLast < 0)
                    {
                        throw new InvalidOperationException(
                            "Normalized buffer access boundaries are missing from the liveness partition.");
                    }
                    for (int segment = first;
                         segment < afterLast;
                         segment++)
                    {
                        ValidateContents(
                            ref resourceContents,
                            segment,
                            readsPriorValue,
                            producesValue,
                            access,
                            graph,
                            pass,
                            "buffer");
                        TrackIndexedProducer(
                            ref producers,
                            segment,
                            readsPriorValue,
                            producesValue,
                            pass,
                            ref prerequisites);
                    }
                    if (producesValue &&
                        bufferImported &&
                        graph.MarkPassRoot(pass))
                    {
                        rootCount++;
                    }
                }
                continue;
            }

            GraphTextureDescription description =
                graph.GetTextureDescription(resource - bufferCount);
            int trackedTextureOrdinal = resource - bufferCount;
            bool textureImported = canonicalTextureRows is not null
                ? canonicalTextureRows[trackedTextureOrdinal].IsImported
                : graph.GetTextureByResourceOrdinal(resource).IsImported;
            for (int accessIndex = firstAccess;
                 accessIndex < afterLastAccess;
                 accessIndex++)
            {
                int accessOrdinal = resourceAccessOrdinals[accessIndex];
                int pass = accessPassOrdinals[accessOrdinal];
                ref readonly PassInputData access = ref (
                    canonicalPassInputs is not null
                        ? ref canonicalPassInputs[accessOrdinal]
                        : ref graph.PassInputs[accessOrdinal]);
                bool readsPriorValue =
                    (access.Flags & GraphAccess.Read) != 0 ||
                    (access.Flags & GraphAccess.Discard) == 0;
                bool producesValue =
                    (access.Flags & GraphAccess.Write) != 0;
                foreach (TextureCell cell in EnumerateCells(access.TextureRange))
                {
                    int cellIndex = cell.Index(description);
                    ValidateContents(
                        ref resourceContents,
                        cellIndex,
                        readsPriorValue,
                        producesValue,
                        access,
                        graph,
                        pass,
                        "texture");
                    TrackIndexedProducer(
                        ref producers,
                        cellIndex,
                        readsPriorValue,
                        producesValue,
                        pass,
                        ref prerequisites);
                }
                if (producesValue &&
                    textureImported &&
                    graph.MarkPassRoot(pass))
                {
                    rootCount++;
                }
            }
        }

        ArenaSlice<int> pending =
            graph.AllocateSlice<int>(passCount, clear: false);
        FinalizeLiveness(
            graph,
            ref prerequisites,
            bufferBoundaries,
            pending,
            rootCount);
    }

    internal static void AnalyzeLivenessReference(
        RenderGraph graph,
        out ArenaSlice<int> accessPassOrdinals,
        out ArenaSlice<int> resourceAccessOffsets,
        out ArenaSlice<int> resourceAccessOrdinals)
    {
        ArgumentNullException.ThrowIfNull(graph);
        int resourceCount = graph.ResourceCount;
        int passCount = graph.Passes.Length;
        int flagCount = checked(
            passCount +
            resourceCount +
            graph.BufferViewCount +
            graph.TextureViewCount +
            graph.AccelerationStructureCount);
        graph.LivenessFlags = graph.AllocateSlice<byte>(flagCount);
        ArenaSlice<int> passWorkspace =
            graph.AllocateSlice<int>(checked(passCount * 2), clear: false);
        graph.RetainingPasses = passWorkspace.Slice(0, passCount);
        graph.RetainingPasses.Span.Fill(-1);
        ArenaSlice<int> prerequisiteMarks = passWorkspace.Slice(passCount, passCount);
        prerequisiteMarks.Span.Clear();

        ArenaSlice<BufferBoundaryIndex> bufferBoundaries =
            BuildBufferBoundaries(graph);
        accessPassOrdinals = default;
        resourceAccessOffsets = default;
        resourceAccessOrdinals = default;
        ArenaSlice<ProducerIndex> producerIndexes =
            graph.AllocateSlice<ProducerIndex>(resourceCount, clear: false);
        ArenaSlice<ContentMask> contents =
            graph.AllocateSlice<ContentMask>(resourceCount, clear: false);
        int cellStorageCount = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            if (!graph.IsResourceWritten(resource)) continue;
            int cellCount;
            if (graph.IsBufferResourceOrdinal(resource))
            {
                cellCount = Math.Max(0, bufferBoundaries[graph.GetBufferOrdinal(resource)].Count - 1);
            }
            else
            {
                GraphTextureDescription texture = graph.GetTextureDescription(graph.GetTextureOrdinal(resource));
                int planes = GraphFormat.HasStencil(texture.Format) ? 2 : 1;
                cellCount = checked(texture.MipLevels * texture.ArrayLayers * planes);
            }
            if (cellCount > 1)
                cellStorageCount = checked(cellStorageCount + cellCount);
        }
        ArenaSlice<int> producerCells =
            graph.AllocateSlice<int>(cellStorageCount, clear: false);
        producerCells.Span.Fill(-1);
        ArenaSlice<byte> contentCells = graph.AllocateSlice<byte>(cellStorageCount);
        int cellStorageOffset = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            if (!graph.IsResourceWritten(resource)) continue;
            int cellCount;
            bool initialized;
            if (graph.IsBufferResourceOrdinal(resource))
            {
                ResourceUnversionedData buffer = graph.GetBufferByResourceOrdinal(resource);
                cellCount = Math.Max(0, bufferBoundaries[graph.GetBufferOrdinal(resource)].Count - 1);
                initialized = buffer.ContentsInitialized ||
                    buffer.IsImported && buffer.ContentsAvailable;
            }
            else
            {
                ResourceUnversionedData textureRow = graph.GetTextureByResourceOrdinal(resource);
                GraphTextureDescription texture = graph.GetTextureDescription(graph.GetTextureOrdinal(resource));
                int planes = GraphFormat.HasStencil(texture.Format) ? 2 : 1;
                cellCount = checked(texture.MipLevels * texture.ArrayLayers * planes);
                initialized = textureRow.IsImported &&
                    textureRow.ContentsAvailable;
            }
            ArenaSlice<int> resourceProducers = default;
            ArenaSlice<byte> resourceContents = default;
            if (cellCount > 1)
            {
                resourceProducers = producerCells.Slice(cellStorageOffset, cellCount);
                resourceContents = contentCells.Slice(cellStorageOffset, cellCount);
                cellStorageOffset = checked(cellStorageOffset + cellCount);
            }
            producerIndexes[resource] = new ProducerIndex(resourceProducers);
            contents[resource] = new ContentMask(resourceContents, initialized);
        }

        ref ArenaColumn<int> prerequisiteRows = ref graph.DependencyRows;
        prerequisiteRows.Clear();
        int rootCount = 0;
        for (int pass = 0; pass < passCount; pass++)
        {
            prerequisiteRows.EnsureAppendCapacity(passCount);
            ref readonly PassData passRow = ref graph.Passes[pass];
            graph.Passes[pass].DependencyOffset = prerequisiteRows.Count;
            graph.Passes[pass].DependencyCount = 0;
            if ((passRow.Flags & PassFlags.NeverCull) != 0 && graph.MarkPassRoot(pass)) rootCount++;
            int stamp = pass + 1;
            foreach (ref readonly PassInputData access in graph.GetPassAccesses(passRow))
            {
                int resource = graph.GetResourceOrdinal(access);
                bool readsPriorValue =
                    (access.Flags & GraphAccess.Read) != 0 ||
                    (access.Flags & GraphAccess.Discard) == 0;
                bool producesValue =
                    (access.Flags & GraphAccess.Write) != 0;
                if (!graph.IsResourceWritten(resource))
                {
                    bool contentsAvailable = access.IsBuffer
                        ? graph.Buffers[access.Buffer].ContentsInitialized ||
                          graph.Buffers[access.Buffer].IsImported && graph.Buffers[access.Buffer].ContentsAvailable
                        : graph.Textures[access.Texture].IsImported &&
                          graph.Textures[access.Texture].ContentsAvailable;
                    if (!contentsAvailable)
                    {
                        throw new InvalidOperationException(access.IsBuffer
                            ? $"Pass '{graph.GetPassName(pass)}' reads buffer content that has not been imported or fully produced."
                            : $"Pass '{graph.GetPassName(pass)}' reads texture content that has not been imported or fully produced.");
                    }
                    continue;
                }
                if (access.IsBuffer)
                {
                    BufferBoundaryIndex boundaries = bufferBoundaries[access.Buffer];
                    ulong accessEnd = checked(access.BufferRange.Offset + access.BufferRange.Size);
                    int first;
                    int afterLast;
                    if (boundaries.Count == 2 &&
                        boundaries[0] == access.BufferRange.Offset &&
                        boundaries[1] == accessEnd)
                    {
                        first = 0;
                        afterLast = 1;
                    }
                    else
                    {
                        first = boundaries.Find(access.BufferRange.Offset);
                        afterLast = boundaries.Find(accessEnd);
                    }
                    if (first < 0 || afterLast < 0)
                        throw new InvalidOperationException("Normalized buffer access boundaries are missing from the liveness partition.");
                    ref ContentMask resourceContents = ref contents[resource];
                    ref ProducerIndex producers = ref producerIndexes[resource];
                    for (int segment = first; segment < afterLast; segment++)
                    {
                        ValidateContents(
                            ref resourceContents,
                            segment,
                            readsPriorValue,
                            producesValue,
                            access,
                            graph,
                            pass,
                            "buffer");
                        TrackProducer(
                            ref producers,
                            segment,
                            readsPriorValue,
                            producesValue,
                            pass,
                            stamp,
                            prerequisiteMarks,
                            ref prerequisiteRows,
                            ref graph.Passes[pass].DependencyCount);
                    }
                }
                else
                {
                    GraphTextureDescription desc = graph.GetTextureDescription(access.Texture);
                    TextureSubresourceRange range = access.TextureRange;
                    ref ProducerIndex producers = ref producerIndexes[resource];
                    ref ContentMask resourceContents = ref contents[resource];
                    int stencilPlaneOffset = checked(desc.MipLevels * desc.ArrayLayers);
                    for (int layer = checked((int)range.FirstArrayLayer); layer < checked((int)(range.FirstArrayLayer + range.ArrayLayerCount)); layer++)
                    for (int mip = checked((int)range.FirstMipLevel); mip < checked((int)(range.FirstMipLevel + range.MipLevelCount)); mip++)
                    {
                        int index = checked(mip + layer * desc.MipLevels);
                        if ((range.Aspects & (TextureAspects.Color | TextureAspects.Depth)) != 0)
                        {
                            ValidateContents(ref resourceContents, index, readsPriorValue, producesValue, access, graph, pass, "texture");
                            TrackProducer(
                                ref producers,
                                index,
                                readsPriorValue,
                                producesValue,
                                pass,
                                stamp,
                                prerequisiteMarks,
                                ref prerequisiteRows,
                                ref graph.Passes[pass].DependencyCount);
                        }
                        if ((range.Aspects & TextureAspects.Stencil) != 0)
                        {
                            int stencilIndex = checked(index + stencilPlaneOffset);
                            ValidateContents(ref resourceContents, stencilIndex, readsPriorValue, producesValue, access, graph, pass, "texture");
                            TrackProducer(
                                ref producers,
                                stencilIndex,
                                readsPriorValue,
                                producesValue,
                                pass,
                                stamp,
                                prerequisiteMarks,
                                ref prerequisiteRows,
                                ref graph.Passes[pass].DependencyCount);
                        }
                    }
                }

                if (producesValue && graph.IsResourceImported(resource) && graph.MarkPassRoot(pass)) rootCount++;
            }

            ref PassData compiledPass = ref graph.Passes[pass];
            prerequisiteRows.GetSpan(compiledPass.DependencyOffset, compiledPass.DependencyCount)
                .Sort();
        }

        FinalizeLiveness(
            graph,
            prerequisiteRows,
            bufferBoundaries,
            prerequisiteMarks,
            rootCount);
    }

    private static void FinalizeLiveness(
        RenderGraph graph,
        ArenaColumn<int> prerequisiteRows,
        ArenaSlice<BufferBoundaryIndex> bufferBoundaries,
        ArenaSlice<int> pending,
        int rootCount)
    {
        int passCount = graph.Passes.Length;
        int pendingCount = 0;
        for (int pass = passCount - 1; pass >= 0; pass--)
        {
            if (!graph.IsPassRoot(pass)) continue;
            graph.MarkPassLive(pass);
            pending[pendingCount++] = pass;
        }
        while (pendingCount != 0)
        {
            int pass = pending[--pendingCount];
            PassData values = graph.Passes[pass];
            for (int index = values.DependencyCount - 1; index >= 0; index--)
            {
                int prerequisite = prerequisiteRows[values.DependencyOffset + index];
                if (graph.IsPassLive(prerequisite)) continue;
                graph.MarkPassLive(prerequisite);
                graph.RetainingPasses[prerequisite] = pass;
                pending[pendingCount++] = prerequisite;
            }
        }
        CompleteLiveness(
            graph,
            bufferBoundaries,
            rootCount);
    }

    private static unsafe void FinalizeLiveness(
        RenderGraph graph,
        ref PassPredecessorTable prerequisites,
        ArenaSlice<BufferBoundaryIndex> bufferBoundaries,
        ArenaSlice<int> pending,
        int rootCount)
    {
        int passCount = graph.Passes.Length;
        byte* liveFlags = graph.LivenessFlags.DangerousPointer;
        int* pendingRows = pending.DangerousPointer;
        int* retainingRows = graph.RetainingPasses.DangerousPointer;
        int pendingCount = 0;
        for (int pass = passCount - 1; pass >= 0; pass--)
        {
            if ((liveFlags[pass] & RenderGraph.PassRootFlag) == 0)
                continue;
            liveFlags[pass] |= RenderGraph.PassLiveFlag;
            pendingRows[pendingCount++] = pass;
        }
        while (pendingCount != 0)
        {
            int pass = pendingRows[--pendingCount];
            ReadOnlySpan<ulong> words = prerequisites.GetWords(pass);
            for (int wordIndex = words.Length - 1; wordIndex >= 0; wordIndex--)
            {
                ulong word = words[wordIndex];
                while (word != 0)
                {
                    int bit = 63 - System.Numerics.BitOperations.LeadingZeroCount(word);
                    word ^= 1UL << bit;
                    int prerequisite = checked((wordIndex << 6) + bit);
                    if ((liveFlags[prerequisite] &
                         RenderGraph.PassLiveFlag) != 0)
                    {
                        continue;
                    }
                    liveFlags[prerequisite] |= RenderGraph.PassLiveFlag;
                    retainingRows[prerequisite] = pass;
                    pendingRows[pendingCount++] = prerequisite;
                }
            }
        }
        CompleteLiveness(
            graph,
            bufferBoundaries,
            rootCount);
    }

    private static unsafe void CompleteLiveness(
        RenderGraph graph,
        ArenaSlice<BufferBoundaryIndex> bufferBoundaries,
        int rootCount)
    {
        int passCount = graph.Passes.Length;
        int resourceCount = graph.ResourceCount;
        int bufferCount = graph.Buffers.Length;
        int bufferViewCount = graph.BufferViewCount;
        int textureViewCount = graph.TextureViewCount;
        byte* liveFlags = graph.LivenessFlags.DangerousPointer;
        PassData* canonicalPassRows =
            graph.Passes.DangerousContiguousPointer;
        PassInputData* canonicalPassInputs =
            graph.PassInputs.DangerousContiguousPointer;
        ResourceUnversionedData* canonicalBufferRows =
            graph.Buffers.DangerousContiguousPointer;
        ResourceUnversionedData* canonicalTextureRows =
            graph.Textures.DangerousContiguousPointer;
        int activeCount = 0;
        for (int pass = 0; pass < passCount; pass++)
            if ((liveFlags[pass] & RenderGraph.PassLiveFlag) != 0)
                activeCount++;
        ArenaSlice<int> activePassOrdinals = graph.AllocateSlice<int>(activeCount, clear: false);
        int* activePassRows = activePassOrdinals.DangerousPointer;
        for (int pass = 0, active = 0; pass < passCount; pass++)
            if ((liveFlags[pass] & RenderGraph.PassLiveFlag) != 0)
                activePassRows[active++] = pass;

        int liveResources = 0;
        int liveViews = 0;
        int materializedBufferViews = 0;
        int materializedTextureViews = 0;
        int materializedAccelerationStructures = 0;
        for (int active = 0; active < activeCount; active++)
        {
            int pass = activePassRows[active];
            PassData passRow = canonicalPassRows is not null
                ? canonicalPassRows[pass]
                : graph.Passes[pass];
            ReadOnlySpan<PassInputData> accesses =
                canonicalPassInputs is not null
                    ? new ReadOnlySpan<PassInputData>(
                        canonicalPassInputs + passRow.AccessOffset,
                        passRow.AccessCount)
                    : graph.GetPassAccesses(passRow);
            foreach (ref readonly PassInputData access in accesses)
            {
                int resource = access.IsBuffer
                    ? access.Buffer
                    : checked(bufferCount + access.Texture);
                int resourceFlag = passCount + resource;
                if ((liveFlags[resourceFlag] &
                     RenderGraph.ResourceLiveFlag) == 0)
                {
                    liveFlags[resourceFlag] |=
                        RenderGraph.ResourceLiveFlag;
                    liveResources++;
                }
                if (access.View < 0) continue;
                int viewFlag;
                if (access.IsBuffer)
                {
                    if (access.State == GraphResourceUsage.AccelerationStructure)
                    {
                        viewFlag = passCount + resourceCount +
                            bufferViewCount + textureViewCount + access.View;
                        if ((liveFlags[viewFlag] &
                             RenderGraph.ViewMaterializedFlag) == 0)
                            materializedAccelerationStructures++;
                    }
                    else
                    {
                        viewFlag =
                            passCount + resourceCount + access.View;
                        if ((liveFlags[viewFlag] &
                             RenderGraph.ViewMaterializedFlag) == 0)
                            materializedBufferViews++;
                    }
                }
                else
                {
                    viewFlag = passCount + resourceCount +
                        bufferViewCount + access.View;
                    if ((liveFlags[viewFlag] &
                         RenderGraph.ViewMaterializedFlag) == 0)
                        materializedTextureViews++;
                }
                if ((liveFlags[viewFlag] &
                     RenderGraph.ViewLiveFlag) == 0)
                    liveViews++;
                liveFlags[viewFlag] |=
                    RenderGraph.ViewLiveFlag |
                    RenderGraph.ViewMaterializedFlag;
            }
        }
        graph.MaterializedBufferViewCount = materializedBufferViews;
        graph.MaterializedTextureViewCount = materializedTextureViews;
        graph.MaterializedAccelerationStructureCount = materializedAccelerationStructures;

        ulong culledTransientBytes = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            if ((liveFlags[passCount + resource] &
                 RenderGraph.ResourceLiveFlag) != 0)
            {
                continue;
            }
            bool imported = resource < bufferCount
                ? (canonicalBufferRows is not null
                    ? canonicalBufferRows[resource].IsImported
                    : graph.GetBufferByResourceOrdinal(resource).IsImported)
                : (canonicalTextureRows is not null
                    ? canonicalTextureRows[resource - bufferCount].IsImported
                    : graph.GetTextureByResourceOrdinal(resource).IsImported);
            if (!imported)
                culledTransientBytes = checked(culledTransientBytes + graph.GetResourceRequirements(resource).Size);
        }
        int declaredViews = checked(
            graph.BufferViewCount +
            graph.TextureViewCount +
            graph.AccelerationStructureCount);
        graph.Culling = new CullingStatistics(
            passCount,
            activeCount,
            passCount - activeCount,
            resourceCount,
            liveResources,
            resourceCount - liveResources,
            declaredViews,
            liveViews,
            declaredViews - liveViews,
            culledTransientBytes,
            rootCount);

        graph.ActivePassOrdinals = activePassOrdinals;
        graph.BufferBoundaries = bufferBoundaries;
        for (int pass = 0; pass < passCount; pass++)
        {
            if ((liveFlags[pass] & RenderGraph.PassLiveFlag) != 0)
                continue;
            ref PassData row = ref (
                canonicalPassRows is not null
                    ? ref canonicalPassRows[pass]
                    : ref graph.Passes[pass]);
            row.DependencyCount = 0;
        }
    }

    private static void TrackProducer(
        ref ProducerIndex producers,
        int index,
        bool readsPriorValue,
        bool producesValue,
        int pass,
        int stamp,
        ArenaSlice<int> prerequisiteMarks,
        ref ArenaColumn<int> prerequisiteRows,
        ref int prerequisiteCount)
    {
        int producer = producers.GetProducer(index);
        if (readsPriorValue && producer >= 0 && prerequisiteMarks[producer] != stamp)
        {
            prerequisiteMarks[producer] = stamp;
            prerequisiteRows.Add(producer);
            prerequisiteCount++;
        }
        if (producesValue) producers.SetProducer(index, pass);
    }

    private static void TrackIndexedProducer(
        ref ProducerIndex producers,
        int index,
        bool readsPriorValue,
        bool producesValue,
        int pass,
        ref PassPredecessorTable prerequisites)
    {
        int producer = producers.GetProducer(index);
        if (readsPriorValue && producer >= 0)
            prerequisites.Add(pass, producer);
        if (producesValue) producers.SetProducer(index, pass);
    }

    private static void ValidateContents(
        ref ContentMask contents,
        int index,
        bool readsPriorValue,
        bool producesValue,
        in PassInputData access,
        RenderGraph graph,
        int pass,
        string resourceKind)
    {
        if (readsPriorValue && !contents.IsInitialized(index))
            throw new InvalidOperationException($"Pass '{graph.GetPassName(pass)}' reads {resourceKind} content that has not been imported or fully produced.");
        if (producesValue && (access.Flags & GraphAccess.Discard) != 0)
            contents.SetInitialized(index, initialized: true);
    }

    private static ArenaSlice<BufferBoundaryIndex> BuildBufferBoundaries(
        RenderGraph graph)
    {
        int bufferCount = graph.Buffers.Length;
        ArenaSlice<int> workspace =
            graph.AllocateSlice<int>(checked(bufferCount * 3), clear: false);
        ArenaSlice<int> counts = workspace.Slice(0, bufferCount);
        ArenaSlice<int> offsets = workspace.Slice(bufferCount, bufferCount);
        ArenaSlice<int> cursors =
            workspace.Slice(checked(bufferCount * 2), bufferCount);
        counts.Span.Clear();
        cursors.Span.Clear();
        for (int pass = 0; pass < graph.Passes.Length; pass++)
        foreach (ref readonly PassInputData access in
                 graph.GetPassAccesses(graph.Passes[pass]))
        {
            int resource = graph.GetResourceOrdinal(access);
            if ((access.Flags & GraphAccess.Write) != 0)
                graph.MarkResourceWritten(resource);
            if (!access.IsBuffer) continue;
            int buffer = access.Buffer;
            counts[buffer] = checked(counts[buffer] + 2);
        }

        int valueCount = 0;
        for (int buffer = 0; buffer < bufferCount; buffer++)
        {
            offsets[buffer] = valueCount;
            if (!graph.IsResourceWritten(
                    graph.GetBufferResourceOrdinal(buffer)))
            {
                counts[buffer] = 0;
                continue;
            }
            valueCount = checked(valueCount + counts[buffer]);
        }
        ArenaSlice<ulong> values =
            graph.AllocateSlice<ulong>(valueCount, clear: false);
        for (int pass = 0; pass < graph.Passes.Length; pass++)
        foreach (ref readonly PassInputData access in
                 graph.GetPassAccesses(graph.Passes[pass]))
        {
            if (!access.IsBuffer) continue;
            int buffer = access.Buffer;
            if (!graph.IsResourceWritten(
                    graph.GetBufferResourceOrdinal(buffer)))
            {
                continue;
            }
            int destination =
                checked(offsets[buffer] + cursors[buffer]);
            values[destination] = access.BufferRange.Offset;
            values[destination + 1] =
                checked(access.BufferRange.Offset +
                        access.BufferRange.Size);
            cursors[buffer] = checked(cursors[buffer] + 2);
        }

        ArenaSlice<BufferBoundaryIndex> result =
            graph.AllocateSlice<BufferBoundaryIndex>(bufferCount);
        for (int buffer = 0; buffer < bufferCount; buffer++)
        {
            int count = counts[buffer];
            if (count == 0) continue;
            int offset = offsets[buffer];
            Span<ulong> resourceValues =
                values.Span.Slice(offset, count);
            if (count == 2)
            {
                result[buffer] = new BufferBoundaryIndex(
                    resourceValues[0],
                    resourceValues[1]);
                continue;
            }
            resourceValues.Sort();
            int uniqueCount = 1;
            for (int index = 1; index < resourceValues.Length; index++)
            {
                ulong value = resourceValues[index];
                if (value == resourceValues[uniqueCount - 1]) continue;
                resourceValues[uniqueCount++] = value;
            }
            result[buffer] = uniqueCount switch
            {
                1 => new BufferBoundaryIndex(resourceValues[0]),
                2 => new BufferBoundaryIndex(
                    resourceValues[0],
                    resourceValues[1]),
                _ => new BufferBoundaryIndex(
                    values.Slice(offset, uniqueCount)),
            };
        }
        return result;
    }

    private static unsafe ArenaSlice<BufferBoundaryIndex> BuildBufferBoundaries(
        RenderGraph graph,
        out ArenaSlice<int> accessPassOrdinals,
        out ArenaSlice<int> resourceAccessOffsets,
        out ArenaSlice<int> resourceAccessOrdinals)
    {
        int bufferCount = graph.Buffers.Length;
        int resourceCount = graph.ResourceCount;
        int passCount = graph.Passes.Length;
        int accessCount = graph.PassInputs.Length;
        PassData* canonicalPassRows =
            graph.Passes.DangerousContiguousPointer;
        PassInputData* canonicalPassInputs =
            graph.PassInputs.DangerousContiguousPointer;
        ArenaSlice<int> workspace = graph.AllocateSlice<int>(
            checked(bufferCount * 3 + resourceCount * 2 + 1),
            clear: false);
        ArenaSlice<int> counts = workspace.Slice(0, bufferCount);
        ArenaSlice<int> offsets = workspace.Slice(bufferCount, bufferCount);
        ArenaSlice<int> cursors = workspace.Slice(checked(bufferCount * 2), bufferCount);
        int resourceWorkspaceOffset = checked(bufferCount * 3);
        resourceAccessOffsets = workspace.Slice(resourceWorkspaceOffset, resourceCount + 1);
        ArenaSlice<int> resourceCursors = workspace.Slice(
            checked(resourceWorkspaceOffset + resourceCount + 1),
            resourceCount);
        Span<int> bufferAccessCounts = counts.Span;
        Span<int> bufferValueOffsets = offsets.Span;
        Span<int> bufferValueCursors = cursors.Span;
        Span<int> resourceOffsets = resourceAccessOffsets.Span;
        Span<int> resourceAccessCursors = resourceCursors.Span;
        bufferAccessCounts.Clear();
        bufferValueCursors.Clear();
        resourceOffsets.Clear();
        accessPassOrdinals = graph.AllocateSlice<int>(accessCount, clear: false);
        int* bufferAccessCountRows = counts.DangerousPointer;
        int* resourceOffsetRows = resourceAccessOffsets.DangerousPointer;
        int* accessPassRows = accessPassOrdinals.DangerousPointer;
        byte* liveFlags = graph.LivenessFlags.DangerousPointer;
        for (int pass = 0; pass < passCount; pass++)
        {
            PassData passRow = canonicalPassRows is not null
                ? canonicalPassRows[pass]
                : graph.Passes[pass];
            int afterLastAccess =
                checked(passRow.AccessOffset + passRow.AccessCount);
            for (int accessOrdinal = passRow.AccessOffset;
                 accessOrdinal < afterLastAccess;
                 accessOrdinal++)
            {
                ref readonly PassInputData access = ref (
                    canonicalPassInputs is not null
                        ? ref canonicalPassInputs[accessOrdinal]
                        : ref graph.PassInputs[accessOrdinal]);
                if (access.View < 0)
                    ValidatePassAccessResourceUsage(graph, pass, access);
                int resource = access.IsBuffer
                    ? access.Buffer
                    : checked(bufferCount + access.Texture);
                accessPassRows[accessOrdinal] = pass;
                resourceOffsetRows[resource] =
                    checked(resourceOffsetRows[resource] + 1);
                if ((access.Flags & GraphAccess.Write) != 0)
                {
                    liveFlags[passCount + resource] |=
                        RenderGraph.ResourceWrittenFlag;
                }
                if (!access.IsBuffer) continue;
                int buffer = access.Buffer;
                bufferAccessCountRows[buffer] =
                    checked(bufferAccessCountRows[buffer] + 2);
            }
        }

        int resourceAccessCount = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            int count = resourceOffsetRows[resource];
            resourceOffsetRows[resource] = resourceAccessCount;
            resourceAccessCursors[resource] = resourceAccessCount;
            resourceAccessCount = checked(resourceAccessCount + count);
        }
        resourceOffsetRows[resourceCount] = resourceAccessCount;
        if (resourceAccessCount != accessCount)
            throw new InvalidOperationException("The resource access index does not cover every canonical access row.");
        resourceAccessOrdinals = graph.AllocateSlice<int>(accessCount, clear: false);

        int valueCount = 0;
        for (int buffer = 0; buffer < bufferCount; buffer++)
        {
            bufferValueOffsets[buffer] = valueCount;
            if ((liveFlags[passCount + buffer] &
                 RenderGraph.ResourceWrittenFlag) == 0)
            {
                bufferAccessCounts[buffer] = 0;
                continue;
            }
            valueCount = checked(valueCount + bufferAccessCounts[buffer]);
        }
        ArenaSlice<ulong> values = graph.AllocateSlice<ulong>(valueCount, clear: false);
        Span<ulong> boundaryValues = values.Span;
        ulong* boundaryValueRows = values.DangerousPointer;
        int* indexedPassInputs = resourceAccessOrdinals.DangerousPointer;
        int* resourceCursorRows = resourceCursors.DangerousPointer;
        int* bufferValueOffsetRows = offsets.DangerousPointer;
        int* bufferValueCursorRows = cursors.DangerousPointer;
        for (int accessOrdinal = 0;
             accessOrdinal < accessCount;
             accessOrdinal++)
        {
            ref readonly PassInputData access = ref (
                canonicalPassInputs is not null
                    ? ref canonicalPassInputs[accessOrdinal]
                    : ref graph.PassInputs[accessOrdinal]);
            int resource = access.IsBuffer
                ? access.Buffer
                : checked(bufferCount + access.Texture);
            indexedPassInputs[resourceCursorRows[resource]++] =
                accessOrdinal;
            if (!access.IsBuffer ||
                (liveFlags[passCount + resource] &
                 RenderGraph.ResourceWrittenFlag) == 0)
            {
                continue;
            }
            int buffer = access.Buffer;
            int destination = checked(
                bufferValueOffsetRows[buffer] +
                bufferValueCursorRows[buffer]);
            boundaryValueRows[destination] =
                access.BufferRange.Offset;
            boundaryValueRows[destination + 1] = checked(
                access.BufferRange.Offset +
                access.BufferRange.Size);
            bufferValueCursorRows[buffer] =
                checked(bufferValueCursorRows[buffer] + 2);
        }

        ArenaSlice<BufferBoundaryIndex> result =
            graph.AllocateSlice<BufferBoundaryIndex>(bufferCount);
        Span<BufferBoundaryIndex> boundaryIndexes = result.Span;
        for (int buffer = 0; buffer < bufferCount; buffer++)
        {
            int count = bufferAccessCounts[buffer];
            if (count == 0) continue;
            int offset = bufferValueOffsets[buffer];
            Span<ulong> resourceValues =
                boundaryValues.Slice(offset, count);
            if (count == 2)
            {
                boundaryIndexes[buffer] = new BufferBoundaryIndex(
                    resourceValues[0],
                    resourceValues[1]);
                continue;
            }
            resourceValues.Sort();
            int uniqueCount = 1;
            for (int index = 1; index < resourceValues.Length; index++)
            {
                ulong value = resourceValues[index];
                if (value == resourceValues[uniqueCount - 1]) continue;
                resourceValues[uniqueCount++] = value;
            }
            boundaryIndexes[buffer] = uniqueCount switch
            {
                1 => new BufferBoundaryIndex(resourceValues[0]),
                2 => new BufferBoundaryIndex(resourceValues[0], resourceValues[1]),
                _ => new BufferBoundaryIndex(values.Slice(offset, uniqueCount)),
            };
        }
        return result;
    }

    private unsafe struct ProducerIndex
    {
        private int _inlineProducer;
        private readonly int* _manyProducers;
        private readonly int _producerCount;

        public ProducerIndex(ArenaSlice<int> manyProducers)
        {
            _inlineProducer = -1;
            _manyProducers = manyProducers.DangerousPointer;
            _producerCount = manyProducers.Length;
        }

        public int GetProducer(int index)
        {
            if (_manyProducers is not null) return _manyProducers[index];
            if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
            return _inlineProducer;
        }

        public readonly int Count =>
            _manyProducers is null ? 1 : _producerCount;

        public void SetProducer(int index, int value)
        {
            if (_manyProducers is not null)
            {
                _manyProducers[index] = value;
                return;
            }
            if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
            _inlineProducer = value;
        }
    }

    private unsafe struct ContentMask
    {
        private byte _inline;
        private readonly byte* _many;
        private readonly int _manyCount;

        internal ContentMask(ArenaSlice<byte> many, bool initialized)
        {
            _inline = initialized ? (byte)1 : (byte)0;
            _many = many.DangerousPointer;
            _manyCount = many.Length;
            if (initialized && !many.IsEmpty) many.Span.Fill(1);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal readonly bool IsInitialized(int index)
        {
            if (_many is not null) return _many[index] != 0;
            if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
            return _inline != 0;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void SetInitialized(int index, bool initialized)
        {
            byte value = initialized ? (byte)1 : (byte)0;
            if (_many is not null)
            {
                _many[index] = value;
                return;
            }
            if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
            _inline = value;
        }

        internal readonly bool HasUninitializedCell =>
            _many is null
                ? _inline == 0
                : new ReadOnlySpan<byte>(
                    _many,
                    _manyCount).Contains((byte)0);
    }

}

internal readonly struct BufferBoundaryIndex
{
    private readonly ulong _first;
    private readonly ulong _second;
    private readonly ArenaSlice<ulong> _many;
    private readonly byte _inlineCount;

    public BufferBoundaryIndex(ulong first)
    {
        _first = first;
        _second = 0;
        _many = default;
        _inlineCount = 1;
    }

    public BufferBoundaryIndex(ulong first, ulong second)
    {
        _first = first;
        _second = second;
        _many = default;
        _inlineCount = 2;
    }

    public BufferBoundaryIndex(ArenaSlice<ulong> many)
    {
        if (many.Length <= 2) throw new ArgumentException("Fragmented boundary storage requires at least three values.", nameof(many));
        _first = 0;
        _second = 0;
        _many = many;
        _inlineCount = 0;
    }

    public int Count => !_many.IsEmpty ? _many.Length : _inlineCount;

    public ulong this[int index] => !_many.IsEmpty
        ? _many[index]
        : index switch
        {
            0 when _inlineCount >= 1 => _first,
            1 when _inlineCount == 2 => _second,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    public int Find(ulong value)
    {
        if (!_many.IsEmpty) return _many.ReadOnlySpan.BinarySearch(value);
        if (_inlineCount >= 1 && _first == value) return 0;
        if (_inlineCount == 2 && _second == value) return 1;
        return -1;
    }
}
