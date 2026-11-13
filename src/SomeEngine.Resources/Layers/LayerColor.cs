using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Resources.Layers;

/// <summary>The premultiplied color and colored transmittance stored for one layer fragment.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2, Size = 12)]
public readonly record struct LayerColor(Half PremultipliedRed, Half PremultipliedGreen, Half PremultipliedBlue,
    Half TransmittanceRed, Half TransmittanceGreen, Half TransmittanceBlue)
{
    public LayerColor(Vector3 premultipliedColor, Vector3 transmittance) : this(
        (Half)premultipliedColor.X, (Half)premultipliedColor.Y, (Half)premultipliedColor.Z,
        (Half)transmittance.X, (Half)transmittance.Y, (Half)transmittance.Z) { }

    public Vector3 PremultipliedColor => new((float)PremultipliedRed, (float)PremultipliedGreen, (float)PremultipliedBlue);
    public Vector3 Transmittance => new((float)TransmittanceRed, (float)TransmittanceGreen, (float)TransmittanceBlue);
}
