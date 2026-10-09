using SomeEngine.ECS.Systems;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class RenderPreparationAccessTests
{
    [Fact]
    public void FailedPreparationAllowsTheFollowingUpdate()
    {
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        var systems = new RenderPrepareSystems(world, extraction);
        var dataLock = new RenderDataLock();
        var system = new FailFirstUpdate();
        systems.Add(system);
        try
        {
            Assert.True(dataLock.TryBeginPrepare(out var failed));
            using (failed)
                Assert.Throws<PreparationFailure>(() => systems.Update(failed));

            Assert.True(dataLock.TryBeginRead(out var read));
            read.Dispose();
            Assert.True(dataLock.TryBeginPrepare(out var retry));
            using (retry)
                systems.Update(retry);
            Assert.Equal(1, system.CompletedUpdates);
        }
        finally
        {
            Assert.True(dataLock.TryBeginPrepare(out var shutdown));
            using (shutdown)
                systems.Shutdown(shutdown);
            systems.Dispose();
        }
    }

    [Fact]
    public async Task RunningPreparationExcludesReadsUntilItsScopeIsReleased()
    {
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        var systems = new RenderPrepareSystems(world, extraction);
        var dataLock = new RenderDataLock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        systems.Add(new WaitForRelease(entered, release));
        Task update = Task.Run(() =>
        {
            Assert.True(dataLock.TryBeginPrepare(out var scope));
            using (scope)
                systems.Update(scope);
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(dataLock.TryBeginRead(out _));
            Assert.False(dataLock.TryBeginPrepare(out _));
        }
        finally
        {
            release.Set();
            await update;
            Assert.True(dataLock.TryBeginPrepare(out var shutdown));
            using (shutdown)
                systems.Shutdown(shutdown);
            systems.Dispose();
        }
        Assert.True(dataLock.TryBeginRead(out var read));
        read.Dispose();
    }

    private sealed class PreparationFailure : Exception;

    private sealed class FailFirstUpdate : ISystem<RenderPrepareSystemContext>
    {
        private bool _failed;
        internal int CompletedUpdates { get; private set; }

        public void OnUpdate(ref RenderPrepareSystemContext context)
        {
            if (!_failed)
            {
                _failed = true;
                throw new PreparationFailure();
            }
            CompletedUpdates++;
        }
    }

    private sealed class WaitForRelease(ManualResetEventSlim entered, ManualResetEventSlim release)
        : ISystem<RenderPrepareSystemContext>
    {
        public void OnUpdate(ref RenderPrepareSystemContext context)
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }
    }
}
