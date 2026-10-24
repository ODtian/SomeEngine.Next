using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Shadows;

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 128)]
public struct VirtualShadowFroxelParameters
{
    public Matrix4x4 ViewToWorld;
    public Vector4 ViewTileScaleBias;
    public float Near;
    public float Radius;
    public float DepthScale1;
    public float ClipToViewScale;
    public float ClipToViewBias;
    public uint Orthographic;
    public uint Width;
    public uint Height;
    public uint AdditionalReceiverLayerCount;
    public uint Enabled;
    public uint HighQualityFrontLayerTranslucency;
    public uint Water;
}
