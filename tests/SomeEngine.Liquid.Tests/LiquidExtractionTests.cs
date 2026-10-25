using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Render;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;

namespace SomeEngine.Liquid.Tests;

public sealed class LiquidExtractionTests
{
    [Fact]
    public void EveryFlowReachesItsOwnRenderingDataAfterTheBatchFinishes()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        using World game = WorldJobs.CreateWorld(scheduler: jobs);
        using RenderWorld render = new();
        using var extraction = new RenderExtractionSystems(render, new LiquidExtraction());
        var first = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = Vector3.Zero };
        var second = new LiquidFlow(scene: "floor", fluid: LiquidFluid.Urine) { Pouring = false, Gravity = Vector3.Zero };
        first.Inject(new(0, 4, 0), Vector3.UnitX, 1e-6);
        second.Inject(new(2, 3, 0), -Vector3.UnitX, 2e-6);
        var firstSource = game.CreateEntity(new LiquidBody(first, new LiquidView(), 0));
        var secondSource = game.CreateEntity(new LiquidBody(second, new LiquidView(), 0));
        LiquidFlow.Step(new LiquidFlow[] { first, second });

        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: true), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: true), adapters, out _);
        using var device = backend.CreateDevice(new DeviceDesc(adapters.First(adapter => !adapter.HardwareAccelerated).Id,
            [new DeviceQueueDesc(QueueType.Graphics)]));
        using var renderDevice = new RenderDevice(backend, device);
        using var coordinator = new RenderFrameCoordinator(renderDevice);

        extraction.Extract(game);
        Apply(extraction, render, coordinator);

        LiquidFrame firstData = render.Read<LiquidRenderData>(extraction.RequireMirror(firstSource)).Data;
        LiquidFrame secondData = render.Read<LiquidRenderData>(extraction.RequireMirror(secondSource)).Data;
        Assert.Equal(1, firstData.Count);
        Assert.Equal(1, secondData.Count);
        Assert.Equal(new Vector4(first.Points[0].Position, 1e-6f), firstData.PositionVolume[0]);
        Assert.Equal(new Vector4(second.Points[0].Position, 2e-6f), secondData.PositionVolume[0]);
        Assert.Equal(first.Material, firstData.Material);
        Assert.Equal(second.Material, secondData.Material);

        game.AcquireSystemVersion();
        LiquidFlow.Step(new LiquidFlow[] { first, second });
        game.Replace(firstSource, new LiquidBody(first, new LiquidView(), 1));
        game.Replace(secondSource, new LiquidBody(second, new LiquidView(), 1));
        extraction.Extract(game);
        Apply(extraction, render, coordinator);

        firstData = render.Read<LiquidRenderData>(extraction.RequireMirror(firstSource)).Data;
        secondData = render.Read<LiquidRenderData>(extraction.RequireMirror(secondSource)).Data;
        Assert.Equal(new Vector4(first.Points[0].Position, 1e-6f), firstData.PositionVolume[0]);
        Assert.Equal(new Vector4(second.Points[0].Position, 2e-6f), secondData.PositionVolume[0]);
    }

    private static void Apply(RenderExtractionSystems extraction, RenderWorld render, RenderFrameCoordinator coordinator)
    {
        var prepare = new RenderPrepareSystems(render, extraction);
        Assert.True(coordinator.TryBeginPrepare(out var scope));
        using (scope)
        {
            try { prepare.ApplyExtraction(scope); }
            finally { prepare.Shutdown(scope); prepare.Dispose(); }
            scope.Commit();
        }
    }
}
