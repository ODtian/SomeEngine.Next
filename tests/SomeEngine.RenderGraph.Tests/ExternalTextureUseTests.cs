using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ExternalTextureUseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherConsumerCanLeaveWithoutBreakingTheOther(bool releaseIntroducerFirst)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Texture texture = CreateTexture(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var keepalives = new[] { new DisposalCount(), new DisposalCount() };
        var uses = new GraphTextureUse[2];
        var passes = new GraphPassId[2];
        var recorded = new List<int>();
        for (int index = 0; index < 2; index++)
        {
            using RenderGraphEdit edit = graph.BeginEdit();
            Assert.False(edit.IsAccepted);
            uses[index] = edit.AcquireExternalTextureUse(texture, Boundary(texture), keepalives[index]);
            passes[index] = edit.AddCopyFramePass("read shared external texture",
                PassQueueSelection.Exact(support.GraphicsQueue), (uses[index].Texture, index),
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition pass, ref (GraphTextureId Texture, int Consumer) data) =>
                    pass.Read(data.Texture, ColorRange, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource),
                (ref CopyPassCommandScope commands, in (GraphTextureId Texture, int Consumer) data) =>
                {
                    Assert.Same(texture, commands.GetTexture(data.Texture));
                    recorded.Add(data.Consumer);
                });
            edit.Commit();
            Assert.True(edit.IsAccepted);
        }
        Assert.NotSame(uses[0], uses[1]);
        Assert.Equal(uses[0].Texture, uses[1].Texture);
        Execute(graph, support);
        Assert.Equal(new[] { 0, 1 }, recorded.Order());

        int first = releaseIntroducerFirst ? 0 : 1;
        int remaining = 1 - first;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(passes[first]);
            edit.ReleaseExternalTextureUse(uses[first]);
            edit.Commit();
        }
        graph.CollectCompleted();
        Assert.Equal(1, keepalives[first].Count);
        Assert.Equal(0, keepalives[remaining].Count);
        Assert.Same(texture, graph.GetExternalTexture(uses[remaining].Texture));
        recorded.Clear();
        Execute(graph, support);
        Assert.Equal(new[] { remaining }, recorded);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            bool rejected = false;
            try { edit.ReleaseExternalTextureUse(uses[first]); }
            catch (InvalidOperationException) { rejected = true; }
            Assert.True(rejected);
            edit.Remove(passes[remaining]);
            edit.ReleaseExternalTextureUse(uses[remaining]);
            edit.Commit();
            Assert.True(edit.IsAccepted);
        }
        Assert.Throws<ArgumentException>(() => graph.GetExternalTexture(uses[0].Texture));
        graph.CollectCompleted();
        graph.CollectCompleted();
        graph.Dispose();
        Assert.All(keepalives, value => Assert.Equal(1, value.Count));
    }

    [Fact]
    public void AbandoningAnAcquireAndReleasePreservesTheAcceptedConsumer()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Texture acceptedTexture = CreateTexture(support);
        using Texture candidateTexture = CreateTexture(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var acceptedKeepalive = new DisposalCount();
        var candidateKeepalive = new DisposalCount();
        GraphTextureUse accepted;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            accepted = edit.AcquireExternalTextureUse(acceptedTexture, Boundary(acceptedTexture), acceptedKeepalive);
            edit.Commit();
        }
        RenderGraphEdit abandoned = graph.BeginEdit();
        GraphTextureUse candidate = abandoned.AcquireExternalTextureUse(candidateTexture, Boundary(candidateTexture), candidateKeepalive);
        abandoned.ReleaseExternalTextureUse(accepted);
        Assert.False(abandoned.IsAccepted);
        abandoned.Dispose();
        abandoned.Dispose();
        Assert.False(abandoned.IsAccepted);
        graph.CollectCompleted();
        Assert.Equal(1, candidateKeepalive.Count);
        Assert.Equal(0, acceptedKeepalive.Count);
        Assert.Same(acceptedTexture, graph.GetExternalTexture(accepted.Texture));
        Assert.Throws<ArgumentException>(() => graph.GetExternalTexture(candidate.Texture));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.ReleaseExternalTextureUse(accepted);
            edit.Commit();
        }
        graph.CollectCompleted();
        graph.Dispose();
        Assert.Equal(1, candidateKeepalive.Count);
        Assert.Equal(1, acceptedKeepalive.Count);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    [InlineData(2, false, true)]
    public void AbandoningManyUsesPreservesRetirementOrderAndAcceptedConsumers(
        int layout, bool failCommit, bool failDisposal)
    {
        const int count = 16;
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Texture acceptedTexture = CreateTexture(support);
        using Texture candidateTexture = CreateTexture(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var retired = new List<int>();
        var acceptedKeepalives = Enumerable.Range(0, count)
            .Select(index => new OrderedDisposal(index + count, retired)).ToArray();
        var candidateKeepalives = Enumerable.Range(0, count)
            .Select(index => new OrderedDisposal(index, retired)).ToArray();
        var disposalFailure = new InvalidOperationException("Candidate retirement failed.");
        if (failDisposal) candidateKeepalives[count / 2].Failure = disposalFailure;
        var accepted = new GraphTextureUse[count];
        var candidates = new GraphTextureUse[count];
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            for (int index = 0; index < count; index++)
                accepted[index] = edit.AcquireExternalTextureUse(
                    acceptedTexture, Boundary(acceptedTexture), acceptedKeepalives[index]);
            edit.Commit();
        }
        ulong version = graph.StructureVersion;
        RenderGraphEdit abandoned = graph.BeginEdit();
        try
        {
            // Layout 0 is the accepted-prefix/survivor-suffix quadratic-shift witness.
            // Layout 1 reverses the groups; layout 2 forces nonself survivor moves.
            if (layout == 0)
                foreach (GraphTextureUse use in accepted) abandoned.ReleaseExternalTextureUse(use);
            for (int index = 0; index < count; index++)
            {
                if (layout == 2) abandoned.ReleaseExternalTextureUse(accepted[index]);
                candidates[index] = abandoned.AcquireExternalTextureUse(
                    candidateTexture, Boundary(candidateTexture), candidateKeepalives[index]);
            }
            if (layout == 1)
                foreach (GraphTextureUse use in accepted) abandoned.ReleaseExternalTextureUse(use);
            // Acquisition and release both track each token; no keepalive may be queued twice.
            foreach (GraphTextureUse use in candidates) abandoned.ReleaseExternalTextureUse(use);
            if (failCommit)
            {
                GraphPassId first = abandoned.AddCopyFramePass("first cyclic pass",
                    PassQueueSelection.Exact(support.GraphicsQueue), 0, default,
                    static (ref PassDefinition pass, ref int data) => { },
                    static (ref CopyPassCommandScope commands, in int data) => { });
                GraphPassId second = abandoned.AddCopyFramePass("second cyclic pass",
                    PassQueueSelection.Exact(support.GraphicsQueue), 0, default,
                    static (ref PassDefinition pass, ref int data) => { },
                    static (ref CopyPassCommandScope commands, in int data) => { });
                abandoned.OrderAfter(first, second);
                abandoned.OrderAfter(second, first);
                InvalidOperationException? failure = null;
                try { abandoned.Commit(); }
                catch (InvalidOperationException error) { failure = error; }
                Assert.NotNull(failure);
            }
            Assert.False(abandoned.IsAccepted);
        }
        finally { abandoned.Dispose(); }
        abandoned.Dispose();
        Assert.False(abandoned.IsAccepted);
        Assert.Equal(version, graph.StructureVersion);
        Assert.Empty(retired);
        if (failDisposal)
        {
            AggregateException failure = Assert.Throws<AggregateException>(() => graph.CollectCompleted());
            Assert.Same(disposalFailure, Assert.Single(failure.InnerExceptions));
        }
        else graph.CollectCompleted();
        Assert.Equal(Enumerable.Range(0, count), retired);
        Assert.All(candidateKeepalives, value => Assert.Equal(1, value.Count));
        Assert.All(acceptedKeepalives, value => Assert.Equal(0, value.Count));
        foreach (GraphTextureUse use in accepted)
            Assert.Same(acceptedTexture, graph.GetExternalTexture(use.Texture));
        foreach (GraphTextureUse use in candidates)
            Assert.Throws<ArgumentException>(() => graph.GetExternalTexture(use.Texture));
        graph.CollectCompleted();
        Assert.Equal(Enumerable.Range(0, count), retired);

        retired.Clear();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            foreach (GraphTextureUse use in accepted) edit.ReleaseExternalTextureUse(use);
            edit.Commit();
        }
        graph.CollectCompleted();
        graph.CollectCompleted();
        graph.Dispose();
        graph.Dispose();
        Assert.Equal(Enumerable.Range(count, count), retired);
        Assert.All(acceptedKeepalives, value => Assert.Equal(1, value.Count));
        Assert.All(candidateKeepalives, value => Assert.Equal(1, value.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AUseAcquiredAndReleasedWithinOneEditIsReleasedOnce(bool commit)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Texture texture = CreateTexture(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var keepalive = new DisposalCount();
        RenderGraphEdit edit = graph.BeginEdit();
        GraphTextureUse use = edit.AcquireExternalTextureUse(texture, Boundary(texture), keepalive);
        edit.ReleaseExternalTextureUse(use);
        if (commit) edit.Commit();
        Assert.Equal(commit, edit.IsAccepted);
        edit.Dispose();
        Assert.Equal(commit, edit.IsAccepted);
        Assert.Throws<ArgumentException>(() => graph.GetExternalTexture(use.Texture));
        graph.CollectCompleted();
        graph.Dispose();
        Assert.Equal(1, keepalive.Count);
    }

    [Fact]
    public void AUseProtectsAnExplicitRegistrationWithoutOwningItsRemoval()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Texture texture = CreateTexture(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var keepalive = new DisposalCount();
        GraphTextureId registered;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            registered = edit.RegisterExternalTexture(texture, Boundary(texture));
            edit.Commit();
        }
        GraphTextureUse use;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            use = edit.AcquireExternalTextureUse(texture, Boundary(texture), keepalive);
            edit.Commit();
        }
        Assert.Equal(registered, use.Texture);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            bool rejected = false;
            try { edit.Remove(registered); }
            catch (InvalidOperationException) { rejected = true; }
            Assert.True(rejected);
            edit.Commit();
            Assert.True(edit.IsAccepted);
        }
        Assert.Same(texture, graph.GetExternalTexture(registered));
        graph.CollectCompleted();
        Assert.Equal(0, keepalive.Count);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.ReleaseExternalTextureUse(use);
            edit.Commit();
        }
        graph.CollectCompleted();
        Assert.Equal(1, keepalive.Count);
        Assert.Same(texture, graph.GetExternalTexture(registered));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.Remove(registered);
            edit.Commit();
        }
        Assert.Throws<ArgumentException>(() => graph.GetExternalTexture(registered));
        graph.Dispose();
        Assert.Equal(1, keepalive.Count);
    }

    [Fact]
    public void ReservedFramesAllowReconciliationButStartedFramesRejectUseChanges()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Texture texture = CreateTexture(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var previousKeepalive = new DisposalCount();
        var acceptedKeepalive = new DisposalCount();
        var rejectedKeepalive = new DisposalCount();
        GraphTextureUse accepted;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            accepted = edit.AcquireExternalTextureUse(texture, Boundary(texture), previousKeepalive);
            edit.Commit();
        }
        Assert.True(graph.TryOpenNextFrame(out var next, TimeSpan.Zero));
        ulong version;
        using (next)
        {
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                GraphTextureUse replacement = edit.AcquireExternalTextureUse(texture, Boundary(texture), acceptedKeepalive);
                edit.ReleaseExternalTextureUse(accepted);
                edit.Commit();
                Assert.True(edit.IsAccepted);
                accepted = replacement;
            }
            version = graph.StructureVersion;
            using RenderGraphFrame frame = graph.BeginFrame(next);
            using RenderGraphEdit activeEdit = graph.BeginEdit();
            bool acquireRejected = false;
            try { _ = activeEdit.AcquireExternalTextureUse(texture, Boundary(texture), rejectedKeepalive); }
            catch (InvalidOperationException) { acquireRejected = true; }
            Assert.True(acquireRejected);
            bool releaseRejected = false;
            try { activeEdit.ReleaseExternalTextureUse(accepted); }
            catch (InvalidOperationException) { releaseRejected = true; }
            Assert.True(releaseRejected);
            activeEdit.Commit();
            Assert.True(activeEdit.IsAccepted);
        }
        Assert.Equal(version, graph.StructureVersion);
        Assert.Same(texture, graph.GetExternalTexture(accepted.Texture));
        graph.CollectCompleted();
        Assert.Equal(1, previousKeepalive.Count);
        Assert.Equal(0, acceptedKeepalive.Count);
        Assert.Equal(0, rejectedKeepalive.Count);
        rejectedKeepalive.Dispose();
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.ReleaseExternalTextureUse(accepted);
            edit.Commit();
        }
        graph.CollectCompleted();
        graph.Dispose();
        Assert.Equal(1, previousKeepalive.Count);
        Assert.Equal(1, acceptedKeepalive.Count);
        Assert.Equal(1, rejectedKeepalive.Count);
    }

    [Fact]
    public void ReleasedUsesWaitForSubmittedWorkEvenAfterANewerFrameIsCancelled()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines);
        using var gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        using Texture texture = CreateTexture(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 2));
        var keepalive = new DisposalCount();
        GraphTextureUse use;
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            use = edit.AcquireExternalTextureUse(texture, Boundary(texture), keepalive);
            pass = edit.AddCopyFramePass("use retained external texture", PassQueueSelection.Exact(support.GraphicsQueue),
                use.Texture, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphTextureId value) =>
                    pass.Read(value, ColorRange, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource),
                static (ref CopyPassCommandScope commands, in GraphTextureId value) => { _ = commands.GetTexture(value); });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        _ = support.Backend.Submit(support.GraphicsQueue, new([], [new(gate, 1)], [], [], []));
        try
        {
            using (RenderGraphFrame frame = graph.BeginFrame()) frame.Execute(completions);
            using (RenderGraphFrame cancelled = graph.BeginFrame()) { }
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                edit.Remove(pass);
                edit.ReleaseExternalTextureUse(use);
                edit.Commit();
            }
            Assert.Throws<ArgumentException>(() => graph.GetExternalTexture(use.Texture));
            graph.CollectCompleted();
            Assert.Equal(0, keepalive.Count);
        }
        finally
        {
            _ = support.Backend.Submit(support.ComputeQueue, new([], [], [], [], [new(gate, 1)]));
            support.Wait(completions);
        }
        graph.CollectCompleted();
        graph.CollectCompleted();
        graph.Dispose();
        Assert.Equal(1, keepalive.Count);
    }

    [Fact]
    public void IncompleteInboundWorkIsRejectedWithoutTransferringKeepalive()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true,
            requiredFeatures: DeviceFeatures.ExternalTimelines);
        using var gate = support.Backend.CreateExternalTimeline(support.Device, 0);
        using Texture texture = CreateTexture(support);
        using var producer = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        using (RenderGraphEdit edit = producer.BeginEdit())
        {
            edit.AddGeneralFramePass("incoming texture work", PassQueueSelection.Exact(support.GraphicsQueue),
                0, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref int data) => { },
                static (ref GeneralPassCommandScope commands, in int data) => { });
            edit.Commit();
        }
        var keepalive = new DisposalCount();
        var completions = new QueueCompletion[producer.MaximumQueueCompletionCount];
        TextureBoundaryState[] incoming = [];
        _ = support.Backend.Submit(support.GraphicsQueue, new([], [new(gate, 1)], [], [], []));
        try
        {
            int count;
            using (RenderGraphFrame frame = producer.BeginFrame()) count = frame.Execute(completions);
            QueueCompletion readyAfter = Assert.Single(completions.Take(count));
            incoming = [new(ColorRange, texture.InitialSync, texture.InitialAccess, texture.InitialLayout,
                ResourceContentState.Defined, support.GraphicsQueue, readyAfter)];
            ulong version = graph.StructureVersion;
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                bool rejected = false;
                try { _ = edit.AcquireExternalTextureUse(texture, incoming, keepalive); }
                catch (InvalidOperationException) { rejected = true; }
                Assert.True(rejected);
                edit.Commit();
                Assert.True(edit.IsAccepted);
            }
            Assert.Equal(version, graph.StructureVersion);
            GraphTextureId registered;
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                registered = edit.RegisterExternalTexture(texture, incoming);
                edit.Commit();
            }
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                bool rejected = false;
                try { _ = edit.AcquireExternalTextureUse(texture, [], keepalive); }
                catch (InvalidOperationException) { rejected = true; }
                Assert.True(rejected);
                edit.Commit();
            }
            Assert.Same(texture, graph.GetExternalTexture(registered));
            graph.CollectCompleted();
            Assert.Equal(0, keepalive.Count);
        }
        finally
        {
            _ = support.Backend.Submit(support.ComputeQueue, new([], [], [], [], [new(gate, 1)]));
            support.Wait(completions);
        }
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            _ = edit.AcquireExternalTextureUse(texture, incoming, keepalive);
            edit.Commit();
        }
        graph.Dispose();
        Assert.Equal(1, keepalive.Count);
        using TextureSrv retainedView = support.Backend.CreateTextureSrv(support.Device,
            new TextureSrvDesc(texture, ColorRange, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
    }

    [Fact]
    public void ClosingTheGraphReleasesEveryRemainingConsumerOnce()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Texture texture = CreateTexture(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        var keepalives = new[] { new DisposalCount(), new DisposalCount() };
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphTextureUse first = edit.AcquireExternalTextureUse(texture, Boundary(texture), keepalives[0]);
            GraphTextureUse second = edit.AcquireExternalTextureUse(texture, Boundary(texture), keepalives[1]);
            Assert.Equal(first.Texture, second.Texture);
            edit.Commit();
        }
        graph.Dispose();
        graph.Dispose();
        Assert.All(keepalives, value => Assert.Equal(1, value.Count));
        using TextureSrv retainedView = support.Backend.CreateTextureSrv(support.Device,
            new TextureSrvDesc(texture, ColorRange, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
    }

    private static TextureSubresourceRange ColorRange => new(0, 1, 0, 1, TextureAspects.Color);

    private static Texture CreateTexture(WarpGraphTestSupport support) => support.Backend.CreateTexture(support.Device,
        new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopySource | TextureUsages.CopyDestination));

    private static TextureBoundaryState[] Boundary(Texture texture) =>
        [new(ColorRange, texture.InitialSync, texture.InitialAccess, texture.InitialLayout, ResourceContentState.Defined)];

    private static void Execute(RenderGraph graph, WarpGraphTestSupport support)
    {
        using RenderGraphFrame frame = graph.BeginFrame();
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int count = frame.Execute(completions);
        support.Wait(completions.AsSpan(0, count));
    }

    private sealed class OrderedDisposal(int id, List<int> retired) : IDisposable
    {
        public int Count { get; private set; }
        public Exception? Failure { get; set; }
        public void Dispose()
        {
            Count++;
            retired.Add(id);
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class DisposalCount : IDisposable
    {
        public int Count { get; private set; }
        public void Dispose() => Count++;
    }
}
