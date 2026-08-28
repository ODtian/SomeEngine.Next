using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Lighting;

/// <summary>Exact shared GPU record for render-light assignment and lighting algorithms.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = SizeInBytes)]
public struct GpuLight
{
    public const int SizeInBytes = 144;

    public Vector3 Position;
    public float Range;
    public Vector3 Direction;
    public float InnerConeCos;
    public Vector3 Color;
    public float Intensity;
    public float OuterConeCos;
    public uint LayerMask;
    public int CookieIndex;
    public float CookieStrength;
    public Matrix4x4 WorldToLightCookie;
    public Vector4 CookieScaleOffset;
}
