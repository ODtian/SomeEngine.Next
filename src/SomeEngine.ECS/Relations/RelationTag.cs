using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Relations;

internal interface IRelationTag { }

/// <summary>
/// 自动 RelationTag。当 entity 拥有至少一个类型 T 的 relation 时，
/// 自动在 archetype 中注册一个 tag，使 Query 可以通过 archetype 过滤。
/// </summary>
/// <remarks>
/// 每种 IRelation 类型 T 对应一个 RelationTag&lt;T&gt; tag。
/// - AddRelation 时自动添加
/// - RemoveRelation 时如果该类型的 relation 数量降为 0，自动移除
/// </remarks>
public struct RelationTag<T> : ITag, IRelationTag where T : struct, IRelation { }

