using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>添加 Table 组件。已存在时抛异常。</summary>
    public void Add<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        PublicComponentMutationGuard.Structural<T>("World.Add");
        _components.Add(entity, in value);
    }

    /// <summary>添加 Tag。已存在时抛异常。</summary>
    public void AddTag<T>(Entity entity)
        where T : struct, ITag
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _components.AddTag<T>(entity);
    }

    /// <summary>移除 Table 组件。不存在时抛异常。</summary>
    public void Remove<T>(Entity entity)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        PublicComponentMutationGuard.Structural<T>("World.Remove");
        _components.Remove<T>(entity);
    }

    /// <summary>移除 Tag。不存在时抛异常。</summary>
    public void RemoveTag<T>(Entity entity)
        where T : struct, ITag
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _components.RemoveTag<T>(entity);
    }

    /// <summary>读取组件值（返回拷贝）。</summary>
    public T Read<T>(Entity entity)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobComponent<T>(WorldStorageAccess.Read);
        return _components.Read<T>(entity);
    }

    /// <summary>整值替换组件（不触发迁移）。</summary>
    public void Replace<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        // OnReplace can observe the old value and OnInsert can observe the new value. Those are
        // the only callbacks CommitReplace executes, so hooks for another component or for an
        // irrelevant event must not upgrade this component-local write to a topology writer.
        if (!HasValueReplaceHookCallbacks(componentId))
        {
            using WorldJobAdmissionScope componentAdmission =
                EnterJobComponent<T>(WorldStorageAccess.Write);
            // Hook registration is a topology writer. Rechecking after this topology-read owner
            // is admitted closes the false-fast-path TOCTOU without charging hook-free writes for
            // the global writer.
            if (!HasValueReplaceHookCallbacks(componentId))
            {
                PublicComponentMutationGuard.Value<T>("World.Replace");
                _components.Replace(entity, in value);
                return;
            }
        }

        using WorldJobAdmissionScope topologyAdmission = EnterJobTopologyWrite();
        PublicComponentMutationGuard.Value<T>("World.Replace");
        _components.Replace(entity, in value);
    }

    /// <summary>检查 entity 是否拥有指定组件/tag。</summary>
    public bool Has<T>(Entity entity)
        where T : struct
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyRead();
        return _components.Has<T>(entity);
    }

    /// <summary>启用 enableable 组件。</summary>
    public void Enable<T>(Entity entity)
        where T : struct, IEnableableComponent
    {
        using WorldJobAdmissionScope admission = EnterJobComponent<T>(WorldStorageAccess.Write);
        _components.WriteEnabled<T>(entity, true);
    }

    /// <summary>禁用 enableable 组件。</summary>
    public void Disable<T>(Entity entity)
        where T : struct, IEnableableComponent
    {
        using WorldJobAdmissionScope admission = EnterJobComponent<T>(WorldStorageAccess.Write);
        _components.WriteEnabled<T>(entity, false);
    }

    /// <summary>读取 enableable 组件状态。</summary>
    public bool IsEnabled<T>(Entity entity)
        where T : struct, IEnableableComponent
    {
        using WorldJobAdmissionScope admission = EnterJobComponent<T>(WorldStorageAccess.Read);
        return _components.IsEnabled<T>(entity);
    }

    internal bool IsEnabledId(Entity entity, int componentId)
    {
        return _components.IsEnabled(entity, componentId);
    }

    internal void WriteEnabledId(Entity entity, int componentId, bool enabled)
    {
        _components.WriteEnabled(entity, componentId, enabled);
    }

    public void ClearRemoved<T>(uint throughVersion)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _components.ClearRemoved<T>(throughVersion);
    }

    internal void AddRelationshipComponent<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        PublicComponentMutationGuard.RelationshipRole<T>(nameof(AddRelationshipComponent));
        _components.Add(entity, in value);
    }

    internal void ReplaceRelationshipComponent<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        PublicComponentMutationGuard.RelationshipRole<T>(nameof(ReplaceRelationshipComponent));
        _components.Replace(entity, in value);
    }

    internal void ReplaceRelationshipComponent<T>(
        Entity entity,
        in T value,
        uint version)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        PublicComponentMutationGuard.RelationshipRole<T>(nameof(ReplaceRelationshipComponent));
        _components.Replace(entity, in value, version);
    }

    internal void RemoveRelationshipComponent<T>(Entity entity)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        PublicComponentMutationGuard.RelationshipRole<T>(nameof(RemoveRelationshipComponent));
        _components.Remove<T>(entity);
    }
}

