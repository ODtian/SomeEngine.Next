using SomeEngine.Resources.Geometry;

namespace SomeEngine.Assets.Importers;

public class RawAttribute(
    string name,
    float[] data,
    int dimension,
    SomeEngine.Resources.Geometry.ValueType targetType,
    byte numComponents,
    bool normalized
)
{
    public string Name = name;
    public SomeEngine.Resources.Geometry.ValueType TargetType = targetType;
    public byte NumComponents = numComponents;
    public bool Normalized = normalized;

    public float[] Data = data; // Flattened data
    public int Dimension = dimension; // Number of floats per element (1, 2, 3, 4) in source
}

