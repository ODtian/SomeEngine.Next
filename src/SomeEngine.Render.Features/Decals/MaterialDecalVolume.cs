using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Features.Decals;

[StructLayout(LayoutKind.Sequential, Size = (int)SizeInBytes)]
internal struct MaterialDecalVolume
{
    internal const uint SizeInBytes = 144;
    internal Matrix4x4 WorldToDecal;
    internal Matrix4x4 DecalToWorld;
    internal uint Parameters;
    internal uint Padding0, Padding1, Padding2;
}
