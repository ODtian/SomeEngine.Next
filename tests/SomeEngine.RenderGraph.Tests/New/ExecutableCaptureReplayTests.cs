using System.Text.Json.Nodes;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ExecutableCaptureReplayTests
{
    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Null_replay_recreates_resources_executes_commands_and_matches_output()
    {
        using NullDevice device = new();
        byte[] expected = Enumerable.Range(0, 32).Select(static value => unchecked((byte)(value * 13 + 5))).ToArray();
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc((ulong)expected.Length, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc((ulong)expected.Length, BufferUsage.CopyDestination),
            MemoryType.Readback);
        device.WriteBuffer(upload, 0, expected);
        try
        {
            Capture capture = ExecuteCopy(device, upload, readback, (ulong)expected.Length);
            int readbackOrdinal = capture.Resources.Single(resource =>
                resource.Buffer is CaptureBufferDescription buffer && buffer.MemoryType == MemoryType.Readback).Ordinal;
            SomeEngine.Graphics.Null.Statistics before = device.Statistics;

            ReplayResult replay = ReplayExecutor.Execute(Capture.FromJson(capture.ToJson(indented: false)), device);

            Assert.Equal(expected, replay.BufferOutputs[readbackOrdinal]);
            Assert.Equal(capture.Batches.Count, replay.ExecutedBatchCount);
            Assert.True(device.Statistics.BufferCreates >= before.BufferCreates + 2);
            Assert.True(device.Statistics.Submissions >= before.Submissions + capture.Batches.Count);
            Assert.Equal(before.ExecutedCopies + 1, device.Statistics.ExecutedCopies);
            Assert.True(device.Statistics.RecordedCommands > before.RecordedCommands);
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
        }
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Corrupt_command_payload_or_resource_contract_fails_closed()
    {
        using NullDevice device = new();
        byte[] payload = { 2, 3, 5, 7, 11, 13, 17, 19 };
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc((ulong)payload.Length, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc((ulong)payload.Length, BufferUsage.CopyDestination),
            MemoryType.Readback);
        device.WriteBuffer(upload, 0, payload);
        try
        {
            Capture capture = ExecuteCopy(device, upload, readback, (ulong)payload.Length);
            string json = capture.ToJson(indented: false);

            JsonObject commandCorruption = JsonNode.Parse(json)!.AsObject();
            JsonObject command = commandCorruption["passes"]!.AsArray()
                .SelectMany(static pass => pass!["commands"]!.AsArray())
                .Single()!.AsObject();
            command["size"] = 999UL;
            Assert.Throws<InvalidOperationException>(() =>
                ReplayExecutor.Execute(Capture.FromJson(commandCorruption.ToJsonString()), device));

            JsonObject resourceCorruption = JsonNode.Parse(json)!.AsObject();
            JsonObject source = resourceCorruption["resources"]!.AsArray()
                .Select(static resource => resource!.AsObject())
                .Single(resource => resource["initialData"]!.GetValue<string>().Length != 0);
            source["buffer"]!["size"] = 1UL;
            Assert.Throws<InvalidOperationException>(() =>
                ReplayExecutor.Execute(Capture.FromJson(resourceCorruption.ToJsonString()), device));
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
        }
    }

    private static Capture ExecuteCopy(
        NullDevice device,
        BufferHandle upload,
        BufferHandle readback,
        ulong size)
    {
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
            EnableCapture = true,
        });
        GraphBuilder builder = graph.Begin();
        BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
        BufferId destination = builder.ImportBuffer(
            readback,
            BufferUse.CopyDestination,
            BufferUse.CopyDestination,
            contentsAvailable: false);
        PassBuilder pass = builder.AddPass("executable-capture-copy", QueueSelection.Copy);
        BufferAccess input = pass.Read(source, BufferUse.CopySource, new BufferRange(0, size));
        BufferAccess output = pass.Write(destination, BufferUse.CopyDestination, new BufferRange(0, size));
        pass.Execute((ICommandContext commands, in PassResources resources) =>
            commands.CopyBuffer(resources.Get(input), 0, resources.Get(output), 0, size));
        GraphExecution execution = graph.Execute(ref builder);
        Assert.True(execution.Wait(TimeSpan.Zero));
        return execution.Capture!;
    }
}
