using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>Indexed model matrices in inherited-prefix then own-bone order.</summary>
public readonly struct ModelTransforms
{
    private readonly BoneSource[] _sources;
    public int Length => _sources.Length;
    public Matrix4x4 this[int bone] => _sources[bone].Transform;

    internal ModelTransforms(BoneSource[] sources) => _sources = sources;

    public void CopyTo(Span<Matrix4x4> destination)
    {
        for (int bone = 0; bone < _sources.Length; bone++) destination[bone] = _sources[bone].Transform;
    }

    public Matrix4x4[] ToArray()
    {
        var result = new Matrix4x4[Length];
        CopyTo(result);
        return result;
    }
}
