using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>添加 Table 组件。已存在时抛异常。</summary>
    public void Add<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        _components.Add(entity, in value);
    }

    /// <summary>添加 Tag。已存在时抛异常。</summary>
    public void AddTag<T>(Entity entity)
        where T : struct, ITag
    {
        _components.AddTag<T>(entity);
    }

    /// <summary>移除 Table 组件。不存在时抛异常。</summary>
    public void Remove<T>(Entity entity)
        where T : struct, IComponent
    {
        _components.Remove<T>(entity);
    }

    /// <summary>移除 Tag。不存在时抛异常。</summary>
    public void RemoveTag<T>(Entity entity)
        where T : struct, ITag
    {
        _components.RemoveTag<T>(entity);
    }

    /// <summary>获取组件的 ref 引用（可原地修改）。</summary>
    public ref T Get<T>(Entity entity)
        where T : struct, IComponent
    {
        return ref _components.Get<T>(entity);
    }

    /// <summary>读取组件值（返回拷贝）。</summary>
    public T Read<T>(Entity entity)
        where T : struct, IComponent
    {
        return _components.Read<T>(entity);
    }

    /// <summary>读取组件只读引用。</summary>
    public ref readonly T ReadRef<T>(Entity entity)
        where T : struct, IComponent
    {
        return ref _components.ReadRef<T>(entity);
    }

    /// <summary>整值替换组件（不触发迁移）。</summary>
    public void Replace<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        _components.Replace(entity, in value);
    }

    /// <summary>检查 entity 是否拥有指定组件/tag。</summary>
    public bool Has<T>(Entity entity)
        where T : struct
    {
        return _components.Has<T>(entity);
    }

    /// <summary>启用 enableable 组件。</summary>
    public void Enable<T>(Entity entity)
        where T : struct, IEnableableComponent
    {
        _components.WriteEnabled<T>(entity, true);
    }

    /// <summary>禁用 enableable 组件。</summary>
    public void Disable<T>(Entity entity)
        where T : struct, IEnableableComponent
    {
        _components.WriteEnabled<T>(entity, false);
    }

    /// <summary>读取 enableable 组件状态。</summary>
    public bool IsEnabled<T>(Entity entity)
        where T : struct, IEnableableComponent
    {
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
        _components.ClearRemoved<T>(throughVersion);
    }
}

