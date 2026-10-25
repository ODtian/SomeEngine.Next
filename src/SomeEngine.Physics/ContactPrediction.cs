using System.Runtime.InteropServices;

namespace SomeEngine.Physics;

/// <summary>Identifies geometry sampled at the common substep start; response time remains on ContactResponse.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public readonly struct ContactPrediction
{
    [FieldOffset(0)] private readonly uint _enabled;
    [FieldOffset(8)] public readonly double SampleTime;

    public bool Enabled => _enabled != 0;

    public ContactPrediction(bool enabled, double sampleTime)
    {
        _enabled = enabled ? 1u : 0u;
        SampleTime = sampleTime;
    }
}
