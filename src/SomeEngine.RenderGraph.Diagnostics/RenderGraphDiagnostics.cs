namespace SomeEngine.RenderGraph.Diagnostics;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

public static class RenderGraphDiagnostics
{
    /// <summary>
    /// Executes one invocation while explicitly requesting a detached diagnostics document.
    /// The detached document is materialized explicitly after execution from the invocation's
    /// canonical rows; ordinary <see cref="RenderGraph.Execute"/> performs no diagnostics callbacks.
    /// </summary>
    public static QueueCompletion[] ExecuteWithSnapshot(
        this RenderGraph graph,
        out RenderGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(graph);
        long started = Stopwatch.GetTimestamp();
        try
        {
            QueueCompletion[] fences = graph.ExecuteForSnapshot(out InvocationCpuTimings timings);
            snapshot = CreateSnapshot(graph, in timings, succeeded: true);
            return fences;
        }
        catch
        {
            snapshot = TryCreateFailureSnapshot(graph, started);
            throw;
        }
    }

    internal static RenderGraphSnapshot CreateCompiledSnapshot(RenderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        RenderGraphSnapshot.Resource[] resources = CopyResources(graph);
        (string[] bufferViews, string[] textureViews, string[] accelerationStructures, string[] shaderArguments) =
            CopyViewsAndShaderArguments(graph);
        (RenderGraphSnapshot.Pass[] passes, RenderGraphSnapshot.Access[] accesses, int[] dependencies) =
            CopyPassesAndAccesses(graph);
        return new RenderGraphSnapshot(
            succeeded: true,
            resources: Transfer(resources),
            bufferViews: Transfer(bufferViews),
            textureViews: Transfer(textureViews),
            accelerationStructures: Transfer(accelerationStructures),
            passes: Transfer(passes),
            accesses: Transfer(accesses),
            shaderArguments: Transfer(shaderArguments),
            dependencies: Transfer(dependencies),
            barriers: Transfer(CopyBarriers(graph)),
            units: Transfer(CopyUnits(graph)),
            tasks: Transfer(CopyTasks(graph)),
            batches: Transfer(CopyBatches(graph)));
    }

    private static RenderGraphSnapshot CreateSnapshot(
        RenderGraph graph,
        in InvocationCpuTimings timings,
        bool succeeded)
    {
        RenderGraphSnapshot.Resource[] resources = CopyResources(graph);
        (string[] bufferViews, string[] textureViews, string[] accelerationStructures, string[] shaderArguments) =
            CopyViewsAndShaderArguments(graph);
        (RenderGraphSnapshot.Pass[] passes, RenderGraphSnapshot.Access[] accesses, int[] dependencies) =
            CopyPassesAndAccesses(graph);
        RenderGraphSnapshot.Batch[] batches = CopyBatches(graph);
        QueueCompletion[] positions = graph.MaterializeBatchPositions();
        for (int ordinal = 0; ordinal < batches.Length && ordinal < positions.Length; ordinal++)
            batches[ordinal] = batches[ordinal] with { Position = ToRow(positions[ordinal]) };

        return new RenderGraphSnapshot(
            succeeded: succeeded,
            resources: Transfer(resources),
            bufferViews: Transfer(bufferViews),
            textureViews: Transfer(textureViews),
            accelerationStructures: Transfer(accelerationStructures),
            passes: Transfer(passes),
            accesses: Transfer(accesses),
            shaderArguments: Transfer(shaderArguments),
            dependencies: Transfer(dependencies),
            barriers: Transfer(CopyBarriers(graph)),
            units: Transfer(CopyUnits(graph)),
            tasks: Transfer(CopyTasks(graph)),
            batches: Transfer(batches),
            timings: MaterializeTimings(graph, in timings));
    }

    private static RenderGraphSnapshot TryCreateFailureSnapshot(
        RenderGraph graph,
        long started)
    {
        try
        {
            InvocationCpuTimings timings = default;
            return CreateSnapshot(graph, in timings, succeeded: false);
        }
        catch
        {
            long finished = Stopwatch.GetTimestamp();
            return new RenderGraphSnapshot(
                succeeded: false,
                timings:
                [
                    new(
                        "FailedInvocation",
                        ClockDomain.ProcessMonotonic,
                        TimeUnit.Nanosecond,
                        0,
                        ToNanoseconds(Stopwatch.GetElapsedTime(started, finished))),
                ]);
        }
    }

    private static ImmutableArray<RenderGraphSnapshot.Timing> MaterializeTimings(
        RenderGraph graph,
        in InvocationCpuTimings timings)
    {
        var rows = new List<RenderGraphSnapshot.Timing>(16);
        long cursor = 0;
        Add("Close", timings.Close);
        Add("Requirements", timings.Compiler.Contents);
        Add("Liveness", timings.Compiler.Liveness);
        Add("Validation", timings.Compiler.Validation);
        Add("Dependencies", timings.Compiler.Dependencies);
        Add("Barrier", timings.Compiler.Barrier);
        Add("Placement", timings.Compiler.Placement);
        Add("ExecutionSchedule", timings.Compiler.Execution);
        Add("ResourceSetup", timings.Acquisition.Setup);
        Add("Heaps", timings.Acquisition.Heaps);
        Add("Resources", timings.Acquisition.Resources);
        Add("Views", timings.Acquisition.Views);
        Add("Bindless", timings.Acquisition.Bindless);
        Add("CommandEncoding", timings.Commands.Encoding);
        Add("Submit", timings.Commands.Submit);
        Add("Cleanup", timings.Commands.Cleanup);
        return Transfer(rows.ToArray());

        void Add(string name, TimeSpan duration)
        {
            long finish = checked(cursor + ToNanoseconds(duration));
            rows.Add(new RenderGraphSnapshot.Timing(
                name,
                ClockDomain.ProcessMonotonic,
                TimeUnit.Nanosecond,
                cursor,
                finish));
            cursor = finish;
        }
    }

    private static long ToNanoseconds(TimeSpan duration) =>
        checked(duration.Ticks * 100L);

    private static RenderGraphSnapshot.Resource[] CopyResources(RenderGraph graph)
    {
        RenderGraphSnapshot.Resource[] resources =
            new RenderGraphSnapshot.Resource[graph.ResourceCount];
        for (int resource = 0; resource < resources.Length; resource++)
        {
            bool buffer = graph.IsBufferResourceOrdinal(resource);
            bool live = graph.IsResourceLive(resource);
            GraphMemoryRequirements requirements = live && !graph.ResourceRequirementRows.IsEmpty
                ? graph.ResourceRequirementRows[resource]
                : default;
            int placementHeap = live && !graph.PlacementHeaps.IsEmpty
                ? graph.PlacementHeaps[resource]
                : -1;
            ulong placementOffset = live && !graph.PlacementOffsets.IsEmpty
                ? graph.PlacementOffsets[resource]
                : 0;
            string? name;
            ulong logicalSize;
            string memoryType;
            if (buffer)
            {
                int ordinal = graph.GetBufferOrdinal(resource);
                BufferDesc desc = graph.GetBufferDescription(ordinal);
                name = desc.Label;
                logicalSize = desc.Size;
                memoryType = graph.Buffers[ordinal].MemoryType.ToString();
            }
            else
            {
                int ordinal = graph.GetTextureOrdinal(resource);
                GraphTextureDescription desc = graph.GetTextureDescription(ordinal);
                name = desc.Label;
                logicalSize = requirements.Size;
                memoryType = requirements.MemoryType.ToString();
            }
            resources[resource] = new(
                resource,
                buffer ? "Buffer" : "Texture",
                name,
                graph.IsResourceImported(resource),
                live,
                logicalSize,
                requirements.Size,
                requirements.Alignment,
                memoryType,
                requirements.Flags.ToString(),
                0,
                placementHeap,
                placementOffset);
        }
        return resources;
    }

    private static (
        RenderGraphSnapshot.Pass[] Passes,
        RenderGraphSnapshot.Access[] Accesses,
        int[] Dependencies) CopyPassesAndAccesses(RenderGraph graph)
    {
        RenderGraphSnapshot.Pass[] passes =
            new RenderGraphSnapshot.Pass[graph.Passes.Count];
        RenderGraphSnapshot.Access[] accesses =
            new RenderGraphSnapshot.Access[graph.PassInputs.Count];
        int[] dependencies = graph.DependencyRows
            .GetReadOnlySpan(0, graph.DependencyRows.Count)
            .ToArray();
        int[] executionOrdinals = new int[graph.Passes.Count];
        executionOrdinals.AsSpan().Fill(-1);
        for (int executionOrdinal = 0;
             executionOrdinal < graph.ActivePassOrdinals.Length;
             executionOrdinal++)
        {
            executionOrdinals[graph.ActivePassOrdinals[executionOrdinal]] = executionOrdinal;
        }
        for (int pass = 0; pass < graph.Passes.Count; pass++)
        {
            PassData row = graph.Passes[pass];
            passes[pass] = new(
                pass,
                executionOrdinals[pass],
                graph.GetPassName(pass),
                graph.Queues[pass],
                row.Flags,
                graph.IsPassLive(pass),
                graph.IsPassRoot(pass),
                row.AccessOffset,
                row.AccessCount,
                row.ShaderArgumentOffset,
                row.ShaderArgumentCount,
                row.DependencyOffset,
                row.DependencyCount);

            for (int local = 0; local < row.AccessCount; local++)
            {
                int ordinal = row.AccessOffset + local;
                PassInputData access = graph.PassInputs[ordinal];
                int resource = graph.GetResourceOrdinal(in access);
                BufferRange bufferRange = access.IsBuffer ? access.BufferRange : default;
                TextureSubresourceRange textureRange = access.IsBuffer ? default : access.TextureRange;
                accesses[ordinal] = new(
                    ordinal,
                    pass,
                    resource,
                    access.View,
                    access.IsBuffer ? "Buffer" : "Texture",
                    access.Flags,
                    access.State,
                    bufferRange.Offset,
                    bufferRange.Size,
                    checked((int)textureRange.FirstMipLevel),
                    checked((int)textureRange.MipLevelCount),
                    checked((int)textureRange.FirstArrayLayer),
                    checked((int)textureRange.ArrayLayerCount),
                    textureRange.Aspects);
            }
        }
        return (passes, accesses, dependencies);
    }

    private static (
        string[] BufferViews,
        string[] TextureViews,
        string[] AccelerationStructures,
        string[] ShaderArguments) CopyViewsAndShaderArguments(RenderGraph graph)
    {
        string[] bufferViews = new string[graph.BufferViewCount];
        for (int ordinal = 0; ordinal < bufferViews.Length; ordinal++)
        {
            BufferRange range = graph.GetBufferViewRange(ordinal);
            Format? format = graph.GetBufferViewFormat(ordinal);
            bufferViews[ordinal] = string.Create(
                CultureInfo.InvariantCulture,
                $"{graph.GetBufferViewResource(ordinal)}|{range.Offset}|{range.Size}|" +
                $"{(int)graph.GetBufferViewType(ordinal)}|" +
                $"{(format.HasValue ? (int)format.Value : -1)}|" +
                $"{graph.GetBufferViewStride(ordinal)}");
        }

        string[] textureViews = new string[graph.TextureViewCount];
        for (int ordinal = 0; ordinal < textureViews.Length; ordinal++)
        {
            TextureSubresourceRange range = graph.GetTextureViewRange(ordinal);
            textureViews[ordinal] = string.Create(
                CultureInfo.InvariantCulture,
                $"{graph.GetTextureViewResource(ordinal)}|{range.FirstMipLevel}|{range.MipLevelCount}|" +
                $"{range.FirstArrayLayer}|{range.ArrayLayerCount}|{(int)range.Aspects}|" +
                $"{(int)graph.GetTextureViewUsage(ordinal)}|" +
                $"{(int)graph.GetTextureViewFormat(ordinal)}|" +
                $"{(int)graph.GetTextureViewDimension(ordinal)}");
        }

        string[] accelerationStructures =
            new string[graph.AccelerationStructureCount];
        for (int ordinal = 0; ordinal < accelerationStructures.Length; ordinal++)
        {
            int buffer = graph.GetAccelerationStructureBuffer(ordinal);
            BufferRange range = graph.GetAccelerationStructureRange(ordinal);
            accelerationStructures[ordinal] = string.Create(
                CultureInfo.InvariantCulture,
                $"{buffer}|{range.Offset}|{range.Size}|" +
                $"{(int)graph.GetAccelerationStructureType(ordinal)}");
        }

        string[] shaderArguments = new string[graph.ShaderArgumentCount];
        for (int ordinal = 0; ordinal < shaderArguments.Length; ordinal++)
        {
            shaderArguments[ordinal] = string.Create(
                CultureInfo.InvariantCulture,
                $"{graph.GetShaderArgumentGroup(ordinal)}|" +
                $"{graph.GetShaderArgumentBinding(ordinal)}|" +
                $"{graph.GetShaderArgumentElement(ordinal)}|" +
                $"{(int)graph.GetShaderArgumentType(ordinal)}|" +
                $"{graph.GetShaderArgumentAccess(ordinal)}|" +
                $"{graph.GetShaderArgumentView(ordinal)}|" +
                $"{graph.GetShaderArgumentSampler(ordinal)}");
        }
        return (bufferViews, textureViews, accelerationStructures, shaderArguments);
    }

    private static RenderGraphSnapshot.Command[] CopyUnits(RenderGraph graph)
    {
        RenderGraphSnapshot.Command[] commands =
            new RenderGraphSnapshot.Command[graph.CommandUnits.Count];
        for (int ordinal = 0; ordinal < commands.Length; ordinal++)
        {
            RuntimeCmd unit = graph.CommandUnits[ordinal];
            commands[ordinal] = new(
                ordinal,
                unit.Name,
                unit.Queue,
                Transfer(graph.GetCommandUnitPasses(unit).ToArray()),
                Transfer(graph.GetCommandUnitDependencies(ordinal).ToArray()),
                unit.AliasCount,
                unit.BarrierCount);
        }
        return commands;
    }

    private static RenderGraphSnapshot.Task[] CopyTasks(RenderGraph graph)
    {
        int commandCount = graph.CommandUnits.Count;
        int firstLaneCount = commandCount <= 1
            ? commandCount
            : checked((commandCount + 1) / 2);
        RenderGraphSnapshot.Task[] tasks =
            new RenderGraphSnapshot.Task[commandCount];
        for (int ordinal = 0; ordinal < tasks.Length; ordinal++)
        {
            RuntimeCmd command = graph.CommandUnits[ordinal];
            tasks[ordinal] = new(
                ordinal,
                command.Queue,
                ordinal < firstLaneCount ? 0 : 1,
                graph.RuntimeCmdRequiresCoordinator(ordinal),
                graph.RuntimeCmdRequiresCoordinator(ordinal),
                ImmutableArray.Create(ordinal),
                command.BarrierCount);
        }
        return tasks;
    }

    private static RenderGraphSnapshot.Batch[] CopyBatches(RenderGraph graph)
    {
        RenderGraphSnapshot.Batch[] batches =
            new RenderGraphSnapshot.Batch[graph.CommandBatches.Count];
        for (int ordinal = 0; ordinal < batches.Length; ordinal++)
        {
            CommandBatch batch = graph.CommandBatches[ordinal];
            ReadOnlySpan<QueueCompletion> waits = graph.GetBatchExternalWaits(batch);
            RenderGraphSnapshot.Fence[] externalWaits = new RenderGraphSnapshot.Fence[waits.Length];
            for (int index = 0; index < waits.Length; index++) externalWaits[index] = ToRow(waits[index]);
            batches[ordinal] = new(
                ordinal,
                batch.Queue,
                Transfer(graph.GetBatchDependencies(batch).ToArray()),
                Transfer(graph.GetBatchCommandUnits(batch).ToArray()),
                Transfer(graph.GetBatchCommandUnits(batch).ToArray()),
                Transfer(externalWaits),
                default);
        }
        return batches;
    }

    private static ImmutableArray<T> Transfer<T>(T[] values) =>
        ImmutableCollectionsMarshal.AsImmutableArray(values);

    private static RenderGraphSnapshot.Barrier[] CopyBarriers(RenderGraph graph)
    {
        List<RenderGraphSnapshot.Barrier> rows = [];
        for (int pass = 0; pass < graph.Passes.Count; pass++)
        {
            foreach (PlannedBarrier barrier in graph.GetBeforeBarriers(pass)) rows.Add(ToRow("BeforePass", pass, in barrier));
            foreach (PlannedBarrier barrier in graph.GetAfterBarriers(pass)) rows.Add(ToRow("AfterPass", pass, in barrier));
        }
        for (int unitOrdinal = 0; unitOrdinal < graph.CommandUnits.Count; unitOrdinal++)
        {
            RuntimeCmd unit = graph.CommandUnits[unitOrdinal];
            foreach (PlannedBarrier barrier in graph.GetCommandUnitBarriers(unit)) rows.Add(ToRow("CommandUnit", unitOrdinal, in barrier));
            foreach (PlannedAliasingBarrier alias in graph.GetCommandUnitAliases(unit))
            {
                rows.Add(new RenderGraphSnapshot.Barrier(
                    "AliasBarrier",
                    unitOrdinal,
                    alias.AfterResource,
                    null,
                    null,
                    null,
                    RenderGraphSnapshot.BarrierKind.Aliasing,
                    null,
                    TransitionOrigin.TrackedResourceState,
                    alias.BeforeResource));
            }
        }
        return [.. rows];
    }

    private static RenderGraphSnapshot.Barrier ToRow(
        string location,
        int owner,
        in PlannedBarrier barrier)
    {
        TransitionOrigin origin = barrier.UsesPlacementInitialState
            ? TransitionOrigin.PlacementInitialState
            : TransitionOrigin.TrackedResourceState;
        return new RenderGraphSnapshot.Barrier(
            location,
            owner,
            barrier.Resource,
            barrier.Before,
            barrier.After,
            barrier.IsTexture ? barrier.TextureRange : null,
            barrier.Kind switch
            {
                GraphBarrierKind.Resource => RenderGraphSnapshot.BarrierKind.Resource,
                GraphBarrierKind.QueueRelease => RenderGraphSnapshot.BarrierKind.QueueRelease,
                GraphBarrierKind.QueueAcquire => RenderGraphSnapshot.BarrierKind.QueueAcquire,
                _ => throw new ArgumentOutOfRangeException(nameof(barrier)),
            },
            barrier.Kind == GraphBarrierKind.Resource ? null : barrier.OtherQueue,
            origin);
    }

    private static RenderGraphSnapshot.Fence ToRow(QueueCompletion position) =>
        new(position.Queue.Type, position.Value);
}
