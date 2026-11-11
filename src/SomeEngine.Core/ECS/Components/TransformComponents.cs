using SomeEngine.Core.Math;
using SomeEngine.ECS;

namespace SomeEngine.Core.ECS.Components;

public struct LocalTransform : IAddedComponent
{
    public TransformQvvs Value;
}

public struct WorldTransform : IAddedComponent
{
    public TransformQvvs Qvvs;
}

