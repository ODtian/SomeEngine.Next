using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>添加 Sparse 组件。不触发 archetype 迁移。</summary>
    public void AddSparse<T>(Entity entity, in T value)
        where T : struct, ISparseComponent
    {
        _sparse.Add(entity, value);
    }

    /// <summary>替换 Sparse 组件。不触发 archetype 迁移。</summary>
    public void ReplaceSparse<T>(Entity entity, in T value)
        where T : struct, ISparseComponent
    {
        _sparse.Replace(entity, value);
    }

    /// <summary>移除 Sparse 组件。</summary>
    public void RemoveSparse<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        _sparse.Remove<T>(entity);
    }

    /// <summary>获取 Sparse 组件 ref 引用。</summary>
    public ref T GetSparse<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        return ref _sparse.Get<T>(entity);
    }

    /// <summary>检查 entity 是否拥有 Sparse 组件。</summary>
    public bool HasSparse<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        return _sparse.Has<T>(entity);
    }

    /// <summary>获取 SparseSet 引用（用于直接迭代 dense 数组）。</summary>
    public SparseSet<T> GetSparseSet<T>()
        where T : struct, ISparseComponent
    {
        return _sparse.Set<T>();
    }
}

