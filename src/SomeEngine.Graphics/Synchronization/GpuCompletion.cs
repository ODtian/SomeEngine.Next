namespace SomeEngine.Graphics;

public readonly record struct GpuCompletion(DeviceDomain Domain, QueueType Queue, ulong Value)
{
    public bool IsValid => Domain.IsValid && Enum.IsDefined(Queue) && Value != 0;
}
