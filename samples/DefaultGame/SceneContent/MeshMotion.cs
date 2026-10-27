using System.Numerics;
using SomeEngine.ECS;

namespace DefaultGame;

/// <summary>Amplitude and deterministic seed for the sample scene's mesh movement.</summary>
public struct MeshMotion : IComponent
{
    public Vector3 Amplitude;
    public uint Seed;
}
