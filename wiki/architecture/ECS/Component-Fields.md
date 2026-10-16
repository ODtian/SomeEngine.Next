# Typed Component Fields

组件根沿 ECS 的 `struct, IComponent`，可以保存托管引用。`IFieldAccess<TComponent, TValue>` 为普通字段、嵌套值字段、当前引用对象内的叶值和可写属性提供相同的静态读写能力。叶值使用 `unmanaged`；生成访问与调用方手写访问共同实现此接口。

```csharp
public interface IFieldAccess<TComponent, TValue>
    where TComponent : struct, IComponent
    where TValue : unmanaged
{
    static abstract TValue Read(in TComponent component);
    static abstract void Write(ref TComponent component, in TValue value);
}
```

`World.ReadField<TComponent, TValue, TAccess>(entity)` 取得当前根的只读引用，并调用具体访问类型。`World.WriteField<TComponent, TValue, TAccess>(entity, in value)` 取得当前根的写引用，记录新版本，再调用具体访问类型。table 与 sparse 根使用各自的实际存储；sparse 根同时实现 `IComponent` 与 `ISparseComponent`。

```csharp
public struct Panel : IComponent
{
    public Style Style;
}

public sealed class Style
{
    public float Radius { get; set; }
}

public struct RadiusAccess : IFieldAccess<Panel, float>
{
    public static float Read(in Panel component) => component.Style.Radius;
    public static void Write(ref Panel component, in float value) => component.Style.Radius = value;
}

world.WriteField<Panel, float, RadiusAccess>(entity, 16f);
float radius = world.ReadField<Panel, float, RadiusAccess>(entity);
```

访问函数在当前访问期间沿实际根与对象路径解析叶值。组件值复制遵循 C# 值和引用语义，引用字段仍指向对应对象。setter 使用 `State.Value` 等公开状态写入时，也执行该状态自己的版本规则。根组件写版本由 ECS 的字段写入入口维护。

## 查询与批次

`QueryRow.ReadField/WriteField<TComponent, TValue, TAccess>()` 用于当前匹配行。`QueryChunkView.ReadField/WriteField<TComponent, TValue, TAccess>(row)` 用于 chunk 中的当前行。

整列批次通过 `ReadFields<TComponent, TValue, TAccess>()` 或 `WriteFields<TComponent, TValue, TAccess>()` 取得类型化字段视图。取得视图时解析查询声明和列位置；批内通过 `Read(row)` 或 `Write(row, in value)` 直接调用具体 `TAccess`，引用路径读取当前根中的对象。

```csharp
static void WriteRadius(QueryCursor query, ref float radius)
{
    foreach (QueryChunkView chunk in query.Chunks)
    {
        var fields = chunk.WriteFields<Panel, float, RadiusAccess>();
        for (int row = 0; row < fields.Count; row++)
            fields.Write(row, in radius);
    }
}

world.ExecuteQuery(query, ref radius, &WriteRadius);
```

字段视图与根组件 span 的有效范围为当前查询回调。整列视图要求 whole-chunk 选择；带行过滤的查询通过 `Rows` 或 `RowIndices` 访问匹配行。`DevelopmentCheck` 在取得字段能力时报告声明、权限与范围错误。

调用方也可从已有 `chunk.Read<TComponent>()`、`chunk.Write<TComponent>()` 或 sparse 读写回调取得根组件 span，再在具体循环中调用 `TAccess`。包含引用的泛型根和缓冲元素继续使用共同的组件、缓冲和容量入口。

## 版本与存储

字段写入沿现有 table/sparse 写权限处理。单实体字段写入取得新 World 写版本；查询字段写入使用查询的 `CurrentSystemVersion`。整列写视图为该列全部行记录本次查询版本，单行写入记录对应行版本。

引用对象内的叶值写入同样推进根的行版本和组件类型写版本。`WriteVersion<T>`、`SparseWriteVersion<T>` 与 `ComponentWriteVersion<T>` 提供公开观察；table 的 `Changed<T>` 使用同一行版本。读取保留已有版本。索引、层级与关系维护复用现有组件写引用和整列写入口。

table 列由 `TComponent[]` 持有。组件操作函数指针使用具体类型赋值进行复制与交换移除，使托管引用沿 C# 赋值和 GC 写屏障语义存储。字节能力与类型化能力共同使用当前组件编号和写版本；字节访问的具体数据约束见 [[architecture/ECS/Component-Bytes|Component Bytes]]。

源码：[接口](../../../src/SomeEngine.ECS/IFieldAccess.cs)、[World 字段入口](../../../src/SomeEngine.ECS/World.Fields.cs)、[字段视图](../../../src/SomeEngine.ECS/Queries/QueryChunkView.Fields.cs)。公开验收：[FieldAccessTests](../../../tests/SomeEngine.ECS.Tests/FieldAccessTests.cs)、[独立消费者](../../../tools/SomeEngine.ECS.AotSmoke/TypedFieldSmoke.cs)。

相关主题：[[architecture/ECS/Change-Tracking|Change Tracking]] · [[architecture/ECS/Systems|Systems]] · [[architecture/animation/Property-Animation|Property Animation]]
