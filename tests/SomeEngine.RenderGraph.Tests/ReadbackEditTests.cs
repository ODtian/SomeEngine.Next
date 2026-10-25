using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;
using Xunit.Abstractions;

namespace SomeEngine.RenderGraph.Tests;

// Batch175 source-version change invalidates the uncertain older post-validation runner's pin.
public sealed class ReadbackEditTests(ITestOutputHelper output)
{
    [Fact]
    public void ColdReadbacksReturnTheirRangesAndTagsInDeliveryOrder()
    {
        D3D12Diagnostics diagnostics;
        using (WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true))
        {
            diagnostics = RequireNativeValidation(support);
            using (var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
                new RenderGraphDesc(MaximumFramesInFlight: 1)))
            {
                var receiver = new ReadbackResults();
                GraphBufferId values;
                GraphReadbackId first, second, third, fourth;
                using (RenderGraphEdit edit = graph.BeginEdit())
                {
                    values = edit.CreatePersistentBuffer(new BufferDesc(32,
                        BufferUsages.CopySource | BufferUsages.CopyDestination));
                    edit.AddBufferUploadPass(values);
                    first = edit.AddReadback(values, new BufferRange(4, 8), receiver, "First range");
                    second = edit.AddReadback(values, new BufferRange(20, 8), receiver, "Second range");
                    third = edit.AddReadback(values, new BufferRange(0, 4), receiver, "Third range");
                    fourth = edit.AddReadback(values, new BufferRange(28, 4), receiver, "Fourth range");
                    edit.Commit();
                }

                var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                int completionCount;
                using (RenderGraphFrame frame = graph.BeginFrame())
                {
                    frame.WriteBuffer(values, 0,
                        MemoryMarshal.AsBytes(new uint[] { 10, 11, 12, 13, 14, 15, 16, 17 }.AsSpan()));
                    frame.SetReadbackTag(first, 12);
                    frame.SetReadbackTag(second, 13);
                    frame.SetReadbackTag(third, 14);
                    frame.SetReadbackTag(fourth, 15);
                    completionCount = frame.Execute(completions);
                }
                support.Wait(completions.AsSpan(0, completionCount));
                graph.CollectCompleted();
                Assert.Collection(receiver.Values,
                    result =>
                    {
                        Assert.Equal(12ul, result.Tag);
                        Assert.Equal(new uint[] { 11, 12 }, MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray());
                    },
                    result =>
                    {
                        Assert.Equal(13ul, result.Tag);
                        Assert.Equal(new uint[] { 15, 16 }, MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray());
                    },
                    result =>
                    {
                        Assert.Equal(14ul, result.Tag);
                        Assert.Equal(new uint[] { 10 }, MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray());
                    },
                    result =>
                    {
                        Assert.Equal(15ul, result.Tag);
                        Assert.Equal(new uint[] { 17 }, MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray());
                    });
                graph.CollectCompleted();
                Assert.Equal(4, receiver.Values.Count);
            }
            Assert.Equal(DeviceStatus.Active, support.Device.Status);
        }
        Assert.Null(diagnostics.TeardownFailure);
    }

    [Fact]
    public void PassOptionChangesPreserveReadbackDelivery()
    {
        D3D12Diagnostics diagnostics;
        using (WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true))
        {
            diagnostics = RequireNativeValidation(support);
            using (var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
                new RenderGraphDesc(MaximumFramesInFlight: 1)))
            {
                var receiver = new ReadbackResults();
                GraphBufferId values;
                GraphPassId ordinary;
                GraphReadbackId first, second;
                using (RenderGraphEdit edit = graph.BeginEdit())
                {
                    values = edit.CreatePersistentBuffer(new BufferDesc(16,
                        BufferUsages.CopySource | BufferUsages.CopyDestination));
                    edit.AddBufferUploadPass(values);
                    ordinary = edit.AddGeneralFramePass("Ordinary pass",
                        PassQueueSelection.Exact(support.GraphicsQueue), 0,
                        new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: false,
                            Recording: PassRecordingMode.CallingThread),
                        static (ref PassDefinition _, ref int _) => { },
                        static (ref GeneralPassCommandScope _, in int _) => { });
                    first = edit.AddReadback(values, new BufferRange(0, 8), receiver, "First half");
                    second = edit.AddReadback(values, new BufferRange(8, 8), receiver, "Second half");
                    edit.Commit();
                }

                var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                int completionCount;
                using (RenderGraphFrame frame = graph.BeginFrame())
                {
                    frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 5, 6, 7, 8 }.AsSpan()));
                    frame.SetReadbackTag(first, 21);
                    frame.SetReadbackTag(second, 22);
                    completionCount = frame.Execute(completions);
                }
                support.Wait(completions.AsSpan(0, completionCount));
                graph.CollectCompleted();
                Assert.Equal(2, receiver.Values.Count);
                Assert.Equal(new uint[] { 5, 6 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 21).Bytes).ToArray());
                Assert.Equal(new uint[] { 7, 8 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 22).Bytes).ToArray());
                graph.CollectCompleted();
                Assert.Equal(2, receiver.Values.Count);

                using (RenderGraphEdit edit = graph.BeginEdit())
                {
                    edit.SetPassOptions(ordinary,
                        new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true,
                            Recording: PassRecordingMode.CallingThread));
                    edit.Commit();
                }
                using (RenderGraphFrame frame = graph.BeginFrame())
                {
                    frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 9, 10, 11, 12 }.AsSpan()));
                    frame.SetReadbackTag(first, 23);
                    frame.SetReadbackTag(second, 24);
                    completionCount = frame.Execute(completions);
                }
                support.Wait(completions.AsSpan(0, completionCount));
                graph.CollectCompleted();
                Assert.Equal(4, receiver.Values.Count);
                Assert.Equal(new uint[] { 5, 6 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 21).Bytes).ToArray());
                Assert.Equal(new uint[] { 7, 8 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 22).Bytes).ToArray());
                Assert.Equal(new uint[] { 9, 10 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 23).Bytes).ToArray());
                Assert.Equal(new uint[] { 11, 12 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 24).Bytes).ToArray());
                graph.CollectCompleted();
                Assert.Equal(4, receiver.Values.Count);
            }
            Assert.Equal(DeviceStatus.Active, support.Device.Status);
        }
        Assert.Null(diagnostics.TeardownFailure);
    }

    [Fact]
    public void RemovingAnOrdinaryPassPreservesEveryReadback()
    {
        D3D12Diagnostics diagnostics;
        using (WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true))
        {
            diagnostics = RequireNativeValidation(support);
            using (var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
                new RenderGraphDesc(MaximumFramesInFlight: 1)))
            {
                var receiver = new ReadbackResults();
                GraphBufferId values;
                GraphPassId ordinary;
                GraphReadbackId first, second;
                using (RenderGraphEdit edit = graph.BeginEdit())
                {
                    values = edit.CreatePersistentBuffer(new BufferDesc(16,
                        BufferUsages.CopySource | BufferUsages.CopyDestination));
                    edit.AddBufferUploadPass(values);
                    ordinary = edit.AddGeneralFramePass("Remove this ordinary pass",
                        PassQueueSelection.Exact(support.GraphicsQueue), 0,
                        new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: false,
                            Recording: PassRecordingMode.CallingThread),
                        static (ref PassDefinition _, ref int _) => { },
                        static (ref GeneralPassCommandScope _, in int _) => { });
                    first = edit.AddReadback(values, new BufferRange(0, 8), receiver, "First half");
                    second = edit.AddReadback(values, new BufferRange(8, 8), receiver, "Last persistent pass");
                    edit.Commit();
                }

                var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                int completionCount;
                using (RenderGraphFrame frame = graph.BeginFrame())
                {
                    frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 1, 2, 3, 4 }.AsSpan()));
                    frame.SetReadbackTag(first, 31);
                    frame.SetReadbackTag(second, 32);
                    completionCount = frame.Execute(completions);
                }
                support.Wait(completions.AsSpan(0, completionCount));
                graph.CollectCompleted();
                Assert.Equal(2, receiver.Values.Count);
                Assert.Equal(new uint[] { 1, 2 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 31).Bytes).ToArray());
                Assert.Equal(new uint[] { 3, 4 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 32).Bytes).ToArray());
                graph.CollectCompleted();
                Assert.Equal(2, receiver.Values.Count);

                using (RenderGraphEdit edit = graph.BeginEdit())
                {
                    edit.Remove(ordinary);
                    edit.Commit();
                }
                using (RenderGraphFrame frame = graph.BeginFrame())
                {
                    frame.WriteBuffer(values, 0, MemoryMarshal.AsBytes(new uint[] { 11, 12, 13, 14 }.AsSpan()));
                    frame.SetReadbackTag(first, 33);
                    frame.SetReadbackTag(second, 34);
                    completionCount = frame.Execute(completions);
                }
                support.Wait(completions.AsSpan(0, completionCount));
                graph.CollectCompleted();
                Assert.Equal(4, receiver.Values.Count);
                Assert.Equal(new uint[] { 1, 2 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 31).Bytes).ToArray());
                Assert.Equal(new uint[] { 3, 4 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 32).Bytes).ToArray());
                Assert.Equal(new uint[] { 11, 12 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 33).Bytes).ToArray());
                Assert.Equal(new uint[] { 13, 14 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 34).Bytes).ToArray());
                graph.CollectCompleted();
                Assert.Equal(4, receiver.Values.Count);
            }
            Assert.Equal(DeviceStatus.Active, support.Device.Status);
        }
        Assert.Null(diagnostics.TeardownFailure);
    }

    [Fact]
    public void ReplacingReadbackKeepsQueuedResultWithItsOriginalReceiver()
    {
        D3D12Diagnostics diagnostics;
        using (WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines))
        {
            diagnostics = RequireNativeValidation(support);
            using (var gate = support.Backend.CreateExternalTimeline(support.Device, 0))
            using (var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
                new RenderGraphDesc(MaximumFramesInFlight: 2)))
            {
                var oldReceiver = new ReadbackResults();
                var newReceiver = new ReadbackResults();
                GraphBufferId values;
                GraphReadbackId oldReadback;
                using (RenderGraphEdit edit = graph.BeginEdit())
                {
                    values = edit.CreatePersistentBuffer(new BufferDesc(16,
                        BufferUsages.CopySource | BufferUsages.CopyDestination));
                    edit.AddBufferUploadPass(values);
                    oldReadback = edit.AddReadback(values, BufferRange.Whole, oldReceiver, "Old readback");
                    edit.Commit();
                }

                var oldCompletions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                int oldCompletionCount = 0;
                GraphReadbackId replacementReadback;
                try
                {
                    _ = support.Backend.Submit(support.GraphicsQueue, new([], [new(gate, 1)], [], [], []));
                    using (RenderGraphFrame frame = graph.BeginFrame())
                    {
                        frame.WriteBuffer(values, 0,
                            MemoryMarshal.AsBytes(new uint[] { 21, 22, 23, 24 }.AsSpan()));
                        frame.SetReadbackTag(oldReadback, 41);
                        oldCompletionCount = frame.Execute(oldCompletions);
                    }
                    using (RenderGraphEdit edit = graph.BeginEdit())
                    {
                        edit.Remove(oldReadback);
                        replacementReadback = edit.AddReadback(values, new BufferRange(4, 8),
                            newReceiver, "Replacement readback");
                        edit.Commit();
                    }
                    graph.CollectCompleted();
                    Assert.Empty(oldReceiver.Values);
                    Assert.Empty(newReceiver.Values);
                }
                finally
                {
                    _ = support.Backend.Submit(support.ComputeQueue, new([], [], [], [], [new(gate, 1)]));
                    support.Wait(oldCompletions.AsSpan(0, oldCompletionCount));
                }

                graph.CollectCompleted();
                var oldResult = Assert.Single(oldReceiver.Values);
                Assert.Equal(41ul, oldResult.Tag);
                Assert.Equal(new uint[] { 21, 22, 23, 24 },
                    MemoryMarshal.Cast<byte, uint>(oldResult.Bytes).ToArray());
                Assert.Empty(newReceiver.Values);
                graph.CollectCompleted();
                Assert.Single(oldReceiver.Values);
                Assert.Empty(newReceiver.Values);

                var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                int completionCount;
                using (RenderGraphFrame frame = graph.BeginFrame())
                {
                    frame.WriteBuffer(values, 0,
                        MemoryMarshal.AsBytes(new uint[] { 31, 32, 33, 34 }.AsSpan()));
                    frame.SetReadbackTag(replacementReadback, 42);
                    completionCount = frame.Execute(completions);
                }
                support.Wait(completions.AsSpan(0, completionCount));
                graph.CollectCompleted();
                var newResult = Assert.Single(newReceiver.Values);
                Assert.Equal(42ul, newResult.Tag);
                Assert.Equal(new uint[] { 32, 33 },
                    MemoryMarshal.Cast<byte, uint>(newResult.Bytes).ToArray());
                oldResult = Assert.Single(oldReceiver.Values);
                Assert.Equal(41ul, oldResult.Tag);
                Assert.Equal(new uint[] { 21, 22, 23, 24 },
                    MemoryMarshal.Cast<byte, uint>(oldResult.Bytes).ToArray());
                graph.CollectCompleted();
                Assert.Single(oldReceiver.Values);
                Assert.Single(newReceiver.Values);
            }
            Assert.Equal(DeviceStatus.Active, support.Device.Status);
        }
        Assert.Null(diagnostics.TeardownFailure);
    }

    [Fact]
    public void CaughtPassDeclarationFailurePreservesReadbacksAfterCommit()
    {
        D3D12Diagnostics diagnostics;
        using (WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true))
        {
            diagnostics = RequireNativeValidation(support);
            using (var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
                new RenderGraphDesc(MaximumFramesInFlight: 1)))
            {
                var receiver = new ReadbackResults();
                GraphBufferId values;
                GraphReadbackId first, second;
                using (RenderGraphEdit edit = graph.BeginEdit())
                {
                    values = edit.CreatePersistentBuffer(new BufferDesc(16,
                        BufferUsages.CopySource | BufferUsages.CopyDestination));
                    edit.AddBufferUploadPass(values);
                    first = edit.AddReadback(values, new BufferRange(0, 8), receiver, "First half");
                    second = edit.AddReadback(values, new BufferRange(8, 8), receiver, "Second half");
                    edit.Commit();
                }

                var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                int completionCount;
                using (RenderGraphFrame frame = graph.BeginFrame())
                {
                    frame.WriteBuffer(values, 0,
                        MemoryMarshal.AsBytes(new uint[] { 41, 42, 43, 44 }.AsSpan()));
                    frame.SetReadbackTag(first, 51);
                    frame.SetReadbackTag(second, 52);
                    completionCount = frame.Execute(completions);
                }
                support.Wait(completions.AsSpan(0, completionCount));
                graph.CollectCompleted();
                Assert.Equal(2, receiver.Values.Count);
                Assert.Equal(new uint[] { 41, 42 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 51).Bytes).ToArray());
                Assert.Equal(new uint[] { 43, 44 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 52).Bytes).ToArray());
                graph.CollectCompleted();
                Assert.Equal(2, receiver.Values.Count);

                var declarationFailure = new InvalidOperationException();
                bool failedCallbackRan = false;
                using (RenderGraphEdit edit = graph.BeginEdit())
                {
                    InvalidOperationException? caught = null;
                    try
                    {
                        _ = edit.AddGeneralFramePass("Throw during declaration",
                            PassQueueSelection.Exact(support.GraphicsQueue), 0,
                            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: false,
                                Recording: PassRecordingMode.CallingThread),
                            (ref PassDefinition _, ref int _) => throw declarationFailure,
                            (ref GeneralPassCommandScope _, in int _) => failedCallbackRan = true);
                    }
                    catch (InvalidOperationException failure)
                    {
                        caught = failure;
                    }
                    Assert.Same(declarationFailure, caught);
                    edit.Commit();
                }
                using (RenderGraphFrame frame = graph.BeginFrame())
                {
                    frame.WriteBuffer(values, 0,
                        MemoryMarshal.AsBytes(new uint[] { 51, 52, 53, 54 }.AsSpan()));
                    frame.SetReadbackTag(first, 53);
                    frame.SetReadbackTag(second, 54);
                    completionCount = frame.Execute(completions);
                }
                support.Wait(completions.AsSpan(0, completionCount));
                graph.CollectCompleted();
                Assert.False(failedCallbackRan);
                Assert.Equal(4, receiver.Values.Count);
                Assert.Equal(new uint[] { 41, 42 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 51).Bytes).ToArray());
                Assert.Equal(new uint[] { 43, 44 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 52).Bytes).ToArray());
                Assert.Equal(new uint[] { 51, 52 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 53).Bytes).ToArray());
                Assert.Equal(new uint[] { 53, 54 }, MemoryMarshal.Cast<byte, uint>(
                    Assert.Single(receiver.Values, result => result.Tag == 54).Bytes).ToArray());
                graph.CollectCompleted();
                Assert.False(failedCallbackRan);
                Assert.Equal(4, receiver.Values.Count);
            }
            Assert.Equal(DeviceStatus.Active, support.Device.Status);
        }
        Assert.Null(diagnostics.TeardownFailure);
    }

    private D3D12Diagnostics RequireNativeValidation(WarpGraphTestSupport support)
    {
        if (!support.Backend.TryGetCapability(support.Device, out D3D12Diagnostics? diagnostics) ||
            diagnostics is null)
        {
            throw new NotSupportedException(
                "BLOCKED_NATIVE_VALIDATION: public D3D12 diagnostics are unavailable.");
        }

        output.WriteLine("DebugLayer={0}; GBV={1}; SynchronizedQueueValidation={2}",
            diagnostics.DebugLayerEnabled,
            diagnostics.GpuBasedValidationEnabled,
            diagnostics.SynchronizedQueueValidationEnabled);
        if (!diagnostics.DebugLayerEnabled ||
            !diagnostics.GpuBasedValidationEnabled ||
            !diagnostics.SynchronizedQueueValidationEnabled)
        {
            throw new NotSupportedException(
                "BLOCKED_NATIVE_VALIDATION: debug layer, GBV and synchronized queue validation are required.");
        }
        return diagnostics;
    }

    private sealed class ReadbackResults : IGraphReadbackReceiver
    {
        internal List<(byte[] Bytes, ulong Tag)> Values { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values.Add((bytes.ToArray(), tag));
    }
}
