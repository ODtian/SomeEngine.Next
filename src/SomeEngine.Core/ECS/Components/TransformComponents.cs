using SomeEngine.Core.Math;
using SomeEngine.ECS;

namespace SomeEngine.Core.ECS.Components;

public struct LocalTransform : IComponent
{
    public TransformQvvs Value;
}

public struct WorldTransform : IComponent
{
    public TransformQvvs Qvvs;
}

