using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Hooks;

namespace SomeEngine.ECS.Commands;

/// <summary>
/// Selects when Parent-to-Children maintenance runs after a recorded mutation is applied.
/// Recording is always deferred until CommandBuffer playback and is independent of this choice.
/// </summary>
public enum HierarchyMaintenanceTiming : byte
{
    Immediate,
    Deferred,
}

public sealed partial class CommandBuffer
{
    public HierarchyCommandWriter<TDomain> Hierarchy<TDomain>()
        where TDomain : IHierarchyDomain
    {
        ValidateRecordAccess();
        return new HierarchyCommandWriter<TDomain>(this);
    }

    public HierarchyCommandWriter<DefaultHierarchyDomain> Hierarchy()
    {
        ValidateRecordAccess();
        return new HierarchyCommandWriter<DefaultHierarchyDomain>(this);
    }

    internal HierarchyCommandWriter<TDomain> Hierarchy<TDomain>(HookCommandToken token)
        where TDomain : IHierarchyDomain
    {
        ValidateRecordAccess(token);
        return new HierarchyCommandWriter<TDomain>(this, token);
    }
}

/// <summary>
/// Typed hierarchy command recorder. It never records generic Parent/Children component writes.
/// </summary>
public ref struct HierarchyCommandWriter<TDomain>
    where TDomain : IHierarchyDomain
{
    private readonly CommandBuffer _buffer;
    private readonly HookCommandToken _token;
    private readonly bool _hasHookToken;

    internal HierarchyCommandWriter(CommandBuffer buffer)
    {
        _buffer = buffer;
        _token = default;
        _hasHookToken = false;
    }

    internal HierarchyCommandWriter(CommandBuffer buffer, HookCommandToken token)
    {
        _buffer = buffer;
        _token = token;
        _hasHookToken = true;
    }

    public void SetParent(
        Entity child,
        Entity parent,
        HierarchyMaintenanceTiming timing = HierarchyMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordSetParent(new CommandEntity(child), new CommandEntity(parent), null, timing);
    }

    public void SetParent(
        DeferredEntity child,
        Entity parent,
        HierarchyMaintenanceTiming timing = HierarchyMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordSetParent(child.AsCommandEntity(_buffer), new CommandEntity(parent), null, timing);
    }

    public void SetParent(
        Entity child,
        DeferredEntity parent,
        HierarchyMaintenanceTiming timing = HierarchyMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordSetParent(new CommandEntity(child), parent.AsCommandEntity(_buffer), null, timing);
    }

    public void SetParent(
        DeferredEntity child,
        DeferredEntity parent,
        HierarchyMaintenanceTiming timing = HierarchyMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordSetParent(child.AsCommandEntity(_buffer), parent.AsCommandEntity(_buffer), null, timing);
    }

    public void SetParent(
        Entity child,
        Entity parent,
        int insertIndex,
        HierarchyMaintenanceTiming timing = HierarchyMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordSetParent(new CommandEntity(child), new CommandEntity(parent), insertIndex, timing);
    }

    public void SetParent(
        DeferredEntity child,
        Entity parent,
        int insertIndex,
        HierarchyMaintenanceTiming timing = HierarchyMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordSetParent(child.AsCommandEntity(_buffer), new CommandEntity(parent), insertIndex, timing);
    }

    public void SetParent(
        Entity child,
        DeferredEntity parent,
        int insertIndex,
        HierarchyMaintenanceTiming timing = HierarchyMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordSetParent(new CommandEntity(child), parent.AsCommandEntity(_buffer), insertIndex, timing);
    }

    public void SetParent(
        DeferredEntity child,
        DeferredEntity parent,
        int insertIndex,
        HierarchyMaintenanceTiming timing = HierarchyMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordSetParent(child.AsCommandEntity(_buffer), parent.AsCommandEntity(_buffer), insertIndex, timing);
    }

    public void Detach(
        Entity child,
        HierarchyMaintenanceTiming timing = HierarchyMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDetach(new CommandEntity(child), timing);
    }

    public void Detach(
        DeferredEntity child,
        HierarchyMaintenanceTiming timing = HierarchyMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDetach(child.AsCommandEntity(_buffer), timing);
    }

    public void Reorder(Entity child, int insertIndex)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new ReorderCommand<TDomain>(new CommandEntity(child), insertIndex));
    }

    public void Reorder(DeferredEntity child, int insertIndex)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new ReorderCommand<TDomain>(child.AsCommandEntity(_buffer), insertIndex));
    }

    public void SetOrderPolicy(Entity parent, ChildOrderPolicy policy)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new SetOrderPolicyCommand<TDomain>(
                new CommandEntity(parent),
                policy,
                ownedPermutation: null));
    }

    public void SetOrderPolicy(DeferredEntity parent, ChildOrderPolicy policy)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new SetOrderPolicyCommand<TDomain>(
                parent.AsCommandEntity(_buffer),
                policy,
                ownedPermutation: null));
    }

    public void SetOrderPolicy(
        Entity parent,
        ChildOrderPolicy policy,
        ReadOnlySpan<Entity> permutation)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new SetOrderPolicyCommand<TDomain>(
                new CommandEntity(parent),
                policy,
                Convert(permutation)));
    }

    public void SetOrderPolicy(
        DeferredEntity parent,
        ChildOrderPolicy policy,
        ReadOnlySpan<Entity> permutation)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new SetOrderPolicyCommand<TDomain>(
                parent.AsCommandEntity(_buffer),
                policy,
                Convert(permutation)));
    }

    public void SetOrderPolicy(
        Entity parent,
        ChildOrderPolicy policy,
        ReadOnlySpan<DeferredEntity> permutation)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new SetOrderPolicyCommand<TDomain>(
                new CommandEntity(parent),
                policy,
                Convert(permutation)));
    }

    public void SetOrderPolicy(
        DeferredEntity parent,
        ChildOrderPolicy policy,
        ReadOnlySpan<DeferredEntity> permutation)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new SetOrderPolicyCommand<TDomain>(
                parent.AsCommandEntity(_buffer),
                policy,
                Convert(permutation)));
    }

    public void DestroySubtree(Entity root)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new DestroySubtreeCommand<TDomain>(new CommandEntity(root)));
    }

    public void DestroySubtree(DeferredEntity root)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new DestroySubtreeCommand<TDomain>(root.AsCommandEntity(_buffer)));
    }

    private void RecordSetParent(
        CommandEntity child,
        CommandEntity parent,
        int? insertIndex,
        HierarchyMaintenanceTiming timing)
    {
        ValidateTiming(timing);
        Record(
            new SetParentCommand<TDomain>(child, parent, insertIndex, timing));
    }

    private void RecordDetach(CommandEntity child, HierarchyMaintenanceTiming timing)
    {
        ValidateTiming(timing);
        Record(new DetachCommand<TDomain>(child, timing));
    }

    private static CommandEntity[] Convert(ReadOnlySpan<Entity> entities)
    {
        var result = new CommandEntity[entities.Length];
        for (int i = 0; i < entities.Length; i++)
            result[i] = new CommandEntity(entities[i]);
        return result;
    }

    private CommandEntity[] Convert(ReadOnlySpan<DeferredEntity> entities)
    {
        var result = new CommandEntity[entities.Length];
        for (int i = 0; i < entities.Length; i++)
            result[i] = entities[i].AsCommandEntity(_buffer);
        return result;
    }

    private static void ValidateTiming(HierarchyMaintenanceTiming timing)
    {
        if (timing != HierarchyMaintenanceTiming.Immediate &&
            timing != HierarchyMaintenanceTiming.Deferred)
        {
            throw new ArgumentOutOfRangeException(nameof(timing), timing, "Unknown hierarchy timing.");
        }
    }
    private CommandBuffer.RecordAccessScope EnterOperation() =>
        _hasHookToken
            ? _buffer.EnterRecordAccess(_token)
            : _buffer.EnterRecordAccess();

    private void Record(ITypedRelationshipCommand command)
    {
        _buffer.RecordTypedRelationshipUnderGate(command);
    }
}

internal interface ITypedRelationshipCommand
{
    void Playback(World world, CommandPlaybackContext context);

    void Cancel();

    void PlaybackFailed();
}

internal abstract class TypedRelationshipCommand : ITypedRelationshipCommand
{
    public abstract void Playback(World world, CommandPlaybackContext context);

    public virtual void Cancel()
    {
    }

    public virtual void PlaybackFailed()
    {
    }
}

internal sealed class SetParentCommand<TDomain> : TypedRelationshipCommand
    where TDomain : IHierarchyDomain
{
    private readonly CommandEntity _child;
    private readonly CommandEntity _parent;
    private readonly int? _insertIndex;
    private readonly HierarchyMaintenanceTiming _timing;

    internal SetParentCommand(
        CommandEntity child,
        CommandEntity parent,
        int? insertIndex,
        HierarchyMaintenanceTiming timing)
    {
        _child = child;
        _parent = parent;
        _insertIndex = insertIndex;
        _timing = timing;
    }

    public override void Playback(World world, CommandPlaybackContext context)
    {
        Entity child = _child.Resolve(context);
        Entity parent = _parent.Resolve(context);
        if (_timing == HierarchyMaintenanceTiming.Deferred)
        {
            if (_insertIndex is int insertIndex)
            {
                SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>.SetParentDeferred(
                    world,
                    child,
                    parent,
                    insertIndex);
            }
            else
            {
                SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>.SetParentDeferred(world, child, parent);
            }
            return;
        }

        if (_insertIndex is int immediateInsertIndex)
            SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>.SetParent(world, child, parent, immediateInsertIndex);
        else
            SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>.SetParent(world, child, parent);
    }
}

internal sealed class DetachCommand<TDomain> : TypedRelationshipCommand
    where TDomain : IHierarchyDomain
{
    private readonly CommandEntity _child;
    private readonly HierarchyMaintenanceTiming _timing;

    internal DetachCommand(CommandEntity child, HierarchyMaintenanceTiming timing)
    {
        _child = child;
        _timing = timing;
    }

    public override void Playback(World world, CommandPlaybackContext context)
    {
        Entity child = _child.Resolve(context);
        if (_timing == HierarchyMaintenanceTiming.Deferred)
            SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>.DetachDeferred(world, child);
        else
            SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>.Detach(world, child);
    }
}

internal sealed class ReorderCommand<TDomain> : TypedRelationshipCommand
    where TDomain : IHierarchyDomain
{
    private readonly CommandEntity _child;
    private readonly int _insertIndex;

    internal ReorderCommand(CommandEntity child, int insertIndex)
    {
        _child = child;
        _insertIndex = insertIndex;
    }

    public override void Playback(World world, CommandPlaybackContext context) =>
        SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>.Reorder(
            world,
            _child.Resolve(context),
            _insertIndex);
}

internal sealed class SetOrderPolicyCommand<TDomain> : TypedRelationshipCommand
    where TDomain : IHierarchyDomain
{
    private readonly CommandEntity _parent;
    private readonly ChildOrderPolicy _policy;
    private readonly CommandEntity[]? _permutation;

    internal SetOrderPolicyCommand(
        CommandEntity parent,
        ChildOrderPolicy policy,
        CommandEntity[]? ownedPermutation)
    {
        _parent = parent;
        _policy = policy;
        _permutation = ownedPermutation;
    }

    public override void Playback(World world, CommandPlaybackContext context)
    {
        Entity parent = _parent.Resolve(context);
        if (_permutation is null)
        {
            SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>.SetChildOrderPolicy(
                world,
                parent,
                _policy);
        }
        else
        {
            var permutation = new Entity[_permutation.Length];
            for (int i = 0; i < permutation.Length; i++)
                permutation[i] = _permutation[i].Resolve(context);
            SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>.SetChildOrderPolicy(
                world,
                parent,
                _policy,
                permutation);
        }
    }
}

internal sealed class DestroySubtreeCommand<TDomain> : TypedRelationshipCommand
    where TDomain : IHierarchyDomain
{
    private readonly CommandEntity _root;

    internal DestroySubtreeCommand(CommandEntity root)
    {
        _root = root;
    }

    public override void Playback(World world, CommandPlaybackContext context) =>
        SomeEngine.ECS.Hierarchy.Hierarchy<TDomain>.DestroySubtree(world, _root.Resolve(context));
}
