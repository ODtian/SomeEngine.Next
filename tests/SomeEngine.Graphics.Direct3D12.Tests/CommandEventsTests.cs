using System.Text;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class CommandEventsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Receiver")]
    [InlineData("接收器 光照 🔆")]
    public void EventLabelsPreserveRepeatedCopyResults(string label)
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer source = backend.CreateBuffer(device,
            new BufferDesc(64, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer destination = backend.CreateBuffer(device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        byte[] labelBytes = Encoding.UTF8.GetBytes(label);

        for (int frame = 0; frame < 3; frame++)
        {
            byte value = checked((byte)(frame + 1));
            using (MappedBuffer mapping = backend.Map(source, MapType.Write, BufferRange.Whole))
            {
                mapping.Bytes.Fill(value);
                mapping.Flush(mapping.Range);
            }

            RecordedCommands commands;
            if (frame == 0)
            {
                backend.Begin(context);
                backend.BeginEvent(context, labelBytes);
                backend.SetMarker(context, labelBytes);
                backend.CopyBuffer(context, new(source, 0, destination, 0, 64));
                backend.EndEvent(context);
                commands = backend.End(context);
            }
            else Assert.True(backend.TryRepeatCommands(context, out commands));

            using (commands)
            {
                QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            }
            backend.CollectCompleted(device);
            using MappedBuffer result = backend.Map(destination, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            Assert.All(result.Bytes.ToArray(), item => Assert.Equal(value, item));
        }
    }
}
