namespace SomeEngine.ECS.Components;

/// <summary>
/// Shared Component 标记接口。
/// Shared Component 的值存储在 World 级中心 store 中，Chunk 仅存储索引。
/// 同一 shared value 的所有 entity 共享同一存储槽。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §4.6 (pending)
/// 典型用例：RenderMesh、SceneId 等。
/// </remarks>
public interface ISharedComponent { }

