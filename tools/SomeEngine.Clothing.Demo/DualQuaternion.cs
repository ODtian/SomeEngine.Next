using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public readonly record struct DualQuaternion(Quaternion Real, Quaternion Dual)
{
    public static DualQuaternion FromRotationTranslation(Quaternion rotation, Vector3 translation)
        => new(rotation, Quaternion.Multiply(new Quaternion(translation, 0), rotation) * .5f);

    public static DualQuaternion operator +(DualQuaternion a, DualQuaternion b)
        => new(a.Real + b.Real, a.Dual + b.Dual);

    public static DualQuaternion operator *(DualQuaternion value, float weight)
        => new(value.Real * weight, value.Dual * weight);

    public static DualQuaternion operator *(DualQuaternion a, DualQuaternion b)
        => new(a.Real * b.Real, a.Real * b.Dual + a.Dual * b.Real);

    public Quaternion Rotation => Real * (1 / Real.Length());

    public Vector3 Translation
    {
        get
        {
            Quaternion translation = Dual * Quaternion.Conjugate(Real);
            return new Vector3(translation.X, translation.Y, translation.Z) * (2 / Real.LengthSquared());
        }
    }

    public Vector3 Transform(Vector3 position) => Vector3.Transform(position, Rotation) + Translation;

    public Vector3 InverseTransform(Vector3 position)
        => Vector3.Transform(position - Translation, Quaternion.Conjugate(Rotation));
}
