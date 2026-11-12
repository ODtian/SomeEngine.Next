namespace SomeEngine.ECS.Comparison;

internal abstract partial class ComparisonWorld
{
    internal ICopyScenario? CopyScenario;
    internal abstract void PrepareCopyForComparison(string workload);
    internal void CopyCycle() => CopyScenario!.Cycle();
}

internal sealed partial class SomeEngineWorld
{
    internal override void PrepareCopyForComparison(string workload)
        => CopyScenario ??= new CopyScenario<SomeEngine.ECS.Entities.Entity, SomeEngine.ECS.Entities.Entity, SomeCopyComparison>(new(_world, _entities, workload == "prefab-set"));
}

internal sealed partial class EasyEcsWorld
{
    internal override void PrepareCopyForComparison(string workload)
        => CopyScenario ??= new CopyScenario<EasyEcs.Entity, EasyEcs.EntityId, EasyCopyComparison>(new(_world, _entities, workload == "prefab-set"));
}

internal sealed partial class FrifloWorld
{
    internal override void PrepareCopyForComparison(string workload)
        => CopyScenario ??= new CopyScenario<Friflo.Engine.ECS.Entity, Friflo.Engine.ECS.RawEntity, NativeCopyComparison<Friflo.Engine.ECS.Entity, Friflo.Engine.ECS.RawEntity, FrifloCopyValues>>(new(new FrifloCopyValues(_world), _entities, workload == "prefab-set"));
}

internal sealed partial class ArchWorld
{
    internal override void PrepareCopyForComparison(string workload)
        => CopyScenario ??= new CopyScenario<Arch.Core.Entity, Arch.Core.Entity, NativeCopyComparison<Arch.Core.Entity, Arch.Core.Entity, ArchCopyValues>>(new(new ArchCopyValues(_world, _entities), _entities, workload == "prefab-set"));
}

internal sealed partial class DefaultEcsWorld
{
    internal override void PrepareCopyForComparison(string workload)
        => CopyScenario ??= new CopyScenario<DefaultEcs.Entity, DefaultEcs.Entity, NativeCopyComparison<DefaultEcs.Entity, DefaultEcs.Entity, DefaultCopyValues>>(new(new DefaultCopyValues(_world), _entities, workload == "prefab-set"));
}
