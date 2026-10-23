using System.Numerics;
using SomeEngine.Resources;

namespace DefaultGame;

/// <summary>Example tint and height-axis displacement, bounded by one normalized attribute unit.</summary>
[MaterialType("DefaultGame.TerrainDisplacementMaterial")]
public sealed partial class TerrainDisplacementMaterial : Material
{
    public partial Vector4 BaseColorTint { get; set; } = Vector4.One;
    public partial float Displacement { get; set; } = 1f;
    public override float BoundsExpansion => MathF.Abs(Displacement);
}
