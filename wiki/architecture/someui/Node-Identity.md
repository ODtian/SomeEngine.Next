# Node Identity

## Definition
节点身份决定一次构建声明的节点与上次的哪个实体对应，由 key 加出现次数确定。
调用位置 key 由源生成器的拦截器提供，各种写法沿同一组身份规则匹配实体。

| 场景 | 身份来源 |
|---|---|
| 普通 struct、Compose、SwiftUI 写法 | 调用位置常量加出现次数；`if`/`else` 两个分支的调用位置不同，身份互相独立 |
| Solid 写法 | 组件只运行一次，身份在创建时确定；`Show`、`For`、`Index` 自行决定子节点身份 |
| 列表 | 显式 `using (Key(id))` |

## Invariants
- 实际身份是 key 加该 key 在当前父节点下本次的出现次数，保存在子实体的 `ElementKey` 组件中。上次的子节点就是层级中的现有子节点，不另存组合记录。
- 底层公开 `View<T>(in T view, ulong key)`；普通 `View<T>(in T view)` 与同名函数的调用位置常量由 #19 接入。显式 key 0 按出现顺序匹配；绕过拦截的单参数重载在 Debug 中通过 `DevelopmentCheck` 报告，在 shipping 中保留 key 0 的后备行为。
- 一个产出带状态节点的辅助函数，在同一位置被有条件地多次调用时，需要 `Key` 或 `[Inline]` 分组。
- `[Inline]` 与返回普通值的可组合函数以调用位置组成前缀，函数体内的 Remember 与节点声明沿此前缀匹配；同一行的不同列分别形成分组。
- 同一父节点下重复 key 的兄弟数量变化且它们带有局部状态时，由开发检查报告。每项使用独立 `Key(id)` 的列表按各项身份增删。

## 身份与子节点匹配实现（#8）

```csharp
public struct ElementKey : IComponent
{
    public ulong Key;
    public int Occurrence;
}

public static Entity View<T>(in T view, ulong key)
    where T : struct, IView, IEquatable<T>;
public static KeyScope Key(ulong key);
```

- 出现次数从 0 开始，在当前父节点本次构建中按最终 key 分别计数。
  默认调用和显式 key 0 共用出现次数；相同 key 的多个 `Key` 块也共用出现次数。
- `using (Scope.Key(id)) Scope.View(new Row(...))` 将该子节点的 key 设为 `id`。
  嵌套 `Key` 按进入顺序组合前缀；块内显式的非零调用 key 与该前缀组合，
  保留同一列表项内各调用位置的身份。最终的 64 位 key 写入 `ElementKey`。
  `KeyScope.Dispose` 恢复外层前缀；子视图自身的构建从自己的父节点上下文开始。
- 每次声明从层级中尚待匹配的子节点查找 `(Key, Occurrence)`。
  类型相同的匹配项复用实体，参数更新沿
  [[architecture/someui/Build-Modes|Build Modes]] 的比较与构建规则处理。
  匹配项的类型变化时，保留旧实体和后代，创建新视图并当场构建；
  新增声明同样创建并当场完成首次构建。
- 本次作用域保存构建开始时的只读子集合与本次声明的子集合。
  两种 [[architecture/someui/Child-Source|Child Source]] 共用这次子声明的恢复边界。
  构建成功时，通过 `Hierarchy<ElementTree>.SetParent(world, child, parent, insertIndex)`
  应用声明次序，外层构建成功后通过 `DestroySubtree` 释放被替换及省略的旧子树。
  嵌套构建共用当次子声明修改记录；失败作用域按倒序恢复子参数与已完成的后代声明，
  释放新增子树并恢复原次序。原有节点身份、类型、参数及原有后代由 ECS 继续持有。
  参数相等时仍保存原值，使自定义 `Equals` 忽略的回调字段也能随失败声明恢复。
  视图参数、`ElementKey` 和 `ElementTree` 持有节点数据；出现次数只在本次构建期间计数。
- `Views.Spawn` 创建的根也带有 `ElementKey`，其值为 key 0、出现次数 0。

`LocalState : ITag` 标记视图自有的局部状态。自定义视图用 ECS 组件保存状态时，
在该视图实体上调用 `world.AddTag<LocalState>(entity)`；状态值继续由其组件持有。
开发检查沿 `ElementTree` 查找该节点和后代中的标记。
已有同 key 的兄弟组数量变化，且变化前或声明后的数量大于 1、组内带局部状态时，
Core `DevelopmentCheck` 报告 key、前后数量与使用独立 `Key` 的提示。
首次创建兄弟组与独立 key 的单项增删沿正常创建、释放路径处理。
数量检查及其构建前计数由 `DEBUG` 条件编译控制，shipping 使用同一身份匹配规则。

公开行为测试见 `tests/SomeEngine.Views.Tests/NodeIdentityTests.cs`；
`FirstBuildChecks.cs` 将 keyed 重排、状态保留、删除和类型替换同步纳入 ECS NativeAOT 冒烟。

## 调用位置实现（#19）

生成器使用 Roslyn `GetInterceptableLocation` 和版本 1 的 `InterceptsLocation`
数据定位调用，编译器将其改写为显式 key 声明。
key 在编译时从文件路径、行、列计算：路径中的反斜杠统一为斜杠，行列从 1 开始，
沿 `#line` 映射后的调用方法名称位置取值，使用 UTF-8 文本的 64 位 FNV-1a。
同一位置的循环调用由出现次数区分；同一行的不同列、不同分支的调用拥有各自的 key。
`Key(id)` 前缀继续与位置 key 组合，列表重排时保留每项内各调用位置的身份。

拦截器通过公开的 `Scope.View(in T, ulong)` 写入同一身份匹配路径。
同名函数声明在生成器当前语义分析中加入编译上下文，使用户源码中的同名函数调用
与该生成器自己的函数体都获得拦截位置；引用程序集提供的同名函数也沿同一入口处理。
编译期诊断用 Roslyn 的实际拦截结果核对每个单参数节点调用；
方法组、委托转换及其它生成器产出的普通节点调用由 `SEV004` 报告。
其它生成器可以直接调用显式 key 重载。

公开行为验收覆盖同名函数与 `View(new ...)` 的参数、分支切换后的局部状态、
同一行双调用删去一个后的实体与状态保留、lambda、泛型参数、可选参数及 sparse 默认。
最小 NativeAOT 示例的发布入口为 `tools/SomeEngine.Views.AotSmoke`。

## 可组合函数分组（#60）

`Scope.GroupKeys(ulong key)` 返回 `GroupKeyScope`，供 Compose 生成入口在原函数体执行期间
组合调用位置前缀。活动 key 前缀用于 Remember 与节点声明，调用分组前缀同时随 `.Content()` 继承，
使多个分组在同一调用方视图上保存的值各自对应原调用位置。
分组退出时恢复外层两种前缀；显式 `Key` 使用所在父节点的上下文，子视图自身构建使用自身上下文。
节点 key 与出现次数、Remember key 与出现次数继续由各自既有路径维护。
分组执行、值返回与跨程序集调用规则见 [[architecture/someui/Compose-Frontend|Compose Frontend]]。

## Relations
[[architecture/someui/Child-Source|Child Source]] · [[architecture/someui/Compose-Frontend|Compose Frontend]] · ADR 0013
