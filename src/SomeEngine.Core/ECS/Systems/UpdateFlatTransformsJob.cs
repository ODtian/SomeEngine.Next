using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Core.ECS.Systems;

internal partial struct UpdateFlatTransformsJob : IJobEntity
{
    public void Execute(in LocalTransform local, ref WorldTransform world) => world.Qvvs = local.Value;
}
