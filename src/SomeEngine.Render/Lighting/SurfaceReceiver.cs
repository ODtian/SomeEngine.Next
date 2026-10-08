using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Lighting;

[Flags]
public enum SurfaceReceiverFlags : uint
{
    Present = 1u << 0,
    Lighting = 1u << 1,
    Subsurface = 1u << 2,
    Hair = 1u << 3,
    FirstPerson = 1u << 4,
    MultipleNormalBases = 1u << 5,
    Water = 1u << 6,
    Translucency = 1u << 7,
}

/// <summary>User-shader receiver data. Depth uses the primary camera's normalized depth convention.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = SizeInBytes)]
public struct SurfaceReceiver
{
    public const int SizeInBytes = 48;
    public Vector3 ShadingNormal;
    public SurfaceReceiverFlags Flags;
    public Vector3 GeometricNormal;
    public float Depth;
    public Vector3 Position;
    public float DepthOffset;
}
