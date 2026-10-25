// PROPOSAL ONLY: not added to the repository, compiled, or executed.
// Candidate destination: tests/SomeEngine.RenderGraph.Tests/RasterCallbackRecoveryTests.cs
// Preserve this as a failing regression on the unmodified baseline, then rerun after the abort fix.
// For an independent direct-D3D12 control, use a separate explicitly named Fact with Create()
// and without claiming native-validation enablement; do not hide both cases in a mode-driven scene.
using System.Numerics;
using SomeEngine.Graphics.Direct3D12;
using Xunit.Abstractions;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RasterCallbackRecoveryTests(ITestOutputHelper output)
{
    [Fact]
    public void NextFrameCompletesAfterRasterCallbackThrows()
    {
        D3D12Diagnostics diagnostics;
        using (WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true))
        {
            if (!support.Backend.TryGetCapability(support.Device, out D3D12Diagnostics? available) ||
                available is null)
            {
                throw new NotSupportedException(
                    "BLOCKED_NATIVE_VALIDATION: public D3D12 diagnostics are unavailable.");
            }

            diagnostics = available;
            output.WriteLine(
                "DebugLayer={0}; GBV={1}; SynchronizedQueueValidation={2}; DRED={3}",
                diagnostics.DebugLayerEnabled,
                diagnostics.GpuBasedValidationEnabled,
                diagnostics.SynchronizedQueueValidationEnabled,
                diagnostics.DredEnabled);
            if (!diagnostics.DebugLayerEnabled ||
                !diagnostics.GpuBasedValidationEnabled ||
                !diagnostics.SynchronizedQueueValidationEnabled)
            {
                throw new NotSupportedException(
                    "BLOCKED_NATIVE_VALIDATION: debug layer, GBV and synchronized queue validation are required.");
            }

            using (var graph = new RenderGraph(
                support.Backend,
                support.Device,
                [support.GraphicsQueue],
                new RenderGraphDesc(MaximumFramesInFlight: 1)))
            {
                bool fail = false;
                int callbackCount = 0;
                var callbackFailure = new InvalidOperationException("raster callback failure");

                using (RenderGraphEdit edit = graph.BeginEdit())
                {
                    TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
                    GraphTextureId texture = edit.CreateTransientTexture(new TextureDesc(
                        TextureDimension.Texture2D, 16, 16, 1, 1, 1, 1,
                        Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment));
                    GraphColorAttachmentViewId view = edit.CreateColorAttachmentView(
                        texture, range, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D);

                    _ = edit.AddRasterPass<GraphColorAttachmentViewId, byte>(
                        "raster callback recovery",
                        PassQueueSelection.Exact(support.GraphicsQueue),
                        view,
                        new PassOptions(
                            Culling: PassCullingMode.NeverCull,
                            Recording: PassRecordingMode.CallingThread,
                            RepeatCommands: false),
                        static (ref PassDefinition access, ref GraphColorAttachmentViewId target) =>
                            access.ColorAttachment(
                                0, target, LoadType.Clear, StoreType.Store,
                                WriteCoverage.Complete, new Vector4(0.125f, 0.25f, 0.5f, 1f)),
                        (ref RasterPassCommandScope commands,
                            in GraphColorAttachmentViewId target, in byte data) =>
                        {
                            callbackCount++;
                            if (fail)
                                throw callbackFailure;
                        });
                    edit.Commit();
                }

                var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                RenderGraphFrameOptions options = new(FrameSubmissionMode.RecordAllThenSubmit);

                // Warmup establishes a completed recording on the graph's only frame position.
                Assert.Equal(1, ExecuteFrame());
                Assert.Equal(1, callbackCount);
                support.Wait(completions);

                // The original exception identity must survive cleanup.
                fail = true;
                Exception? secondFailure = Record.Exception(() => { _ = ExecuteFrame(); });
                Assert.Same(callbackFailure, secondFailure);
                Assert.Equal(2, callbackCount);
                Assert.All(completions, completion => Assert.Equal(default, completion));

                // Do not change graph membership, options, submission mode, or resource ownership.
                fail = false;
                int recoveryCompletionCount = 0;
                Exception? recoveryFailure = Record.Exception(() =>
                {
                    recoveryCompletionCount = ExecuteFrame();
                });
                output.WriteLine(
                    "RecoveryExceptionType={0}; CallbackCount={1}; CompletionCount={2}",
                    recoveryFailure?.GetType().FullName ?? "<none>",
                    callbackCount,
                    recoveryCompletionCount);

                // The baseline is predicted to fail here with callbackCount still 2.
                // This is an intended-recovery assertion, not an assertion accepting the defect.
                Assert.Null(recoveryFailure);
                Assert.Equal(3, callbackCount);
                Assert.Equal(1, recoveryCompletionCount);
                support.Wait(completions);

                int ExecuteFrame()
                {
                    completions.AsSpan().Clear();
                    RenderGraphFrame frame = graph.BeginFrame(options);
                    try
                    {
                        return frame.Execute(completions);
                    }
                    finally
                    {
                        frame.Dispose();
                    }
                }
            }

            Assert.Equal(DeviceStatus.Active, support.Device.Status);
        }

        // Public device diagnostic is useful, but not proof every child Release succeeded.
        // GraphicsObject.Dispose is terminal and may swallow internal Release/observer errors.
        Assert.Null(diagnostics.TeardownFailure);
    }
}
