# Animation Layers

动画层按确定性来源顺序叠加覆盖与位移。基准值由调用方通过 `PropertyAnimation.SetBase` 持有，算法输出由轨道持有，最终字段由对应阶段统一写回。

```text
result = 基准值
for 每个来源（层、Source.Index、Source.Generation、Output）:
    遮罩包含该通道时:
        Override: result = TOps.Lerp(result, Value, Weight)
        Additive: result += TOps.Scale(Value - 基准值, Weight)
```

`AnimationLayers` 在目标上保存最多 8 个层的模式、0–1 权重及遮罩范围。`LayerChannel` 保存组件编号和生成字段编号，`default(LayerMask)` 包含全部通道。部分权重覆盖与叠加层使用该字段的 BaseValue。

## 固定关系与保持值

目标上的 `PropertySource<T>` 是固定来源关系的主要持有者，记录通道、层、动画实体与输出编号，并保存供合成使用的派生输出。`Declare`、图安装及公开 `RefreshSources<T>(world,target)` 在关系变化时排序，解析 BaseValue 索引、遮罩成员和直接/合成方式；轨道保存来源索引。

持续活动轨道逐帧更新自己的输出记录。完成后，来源继续持有最终值与算法历史；基准、层权重、遮罩或来源关系变化时，该输出参加重新合成。相同输入的保持值保留目标组件的呈现写版本。By 的派生输出保存位移参考值，叠加层在基准变化时重新形成相同位移，结束值回到最新基准。

同一目标、通道与层的 To 复用对应值来源；By 使用声明容量中的独立来源。`RemoveSource<T>(world,target,animation,output)` 释放对应算法状态并移除关系，随后重新解析该字段范围。层与遮罩结构显式修改后可通过 RefreshSources 同步关系。

## 当帧贡献

外部系统沿共同 Contribution 入口提交当帧动态来源：

```csharp
PropertyAnimation.DeclareContributions<float>(world, node, capacity: 8);
PropertyAnimation.Contribute(world, node, new Contribution<float>
{
    Channel = PanelDataChannels.Opacity,
    Layer = 1,
    Value = 0.8f,
    Source = effect,
    Output = 0,
});
```

公开 `DynamicBuffer<Contribution<T>>` 写入入口也采用同一数据。Source 与 Output 标识来源输出，同字段同层的来源标识唯一。当帧贡献按 `(Component,Field,Layer,Source.Index,Source.Generation,Output)` 整理；固定来源保留声明时的顺序，合成执行归并。来源求值系统的注册顺序保持相同最终值。

## 阶段与批次

层权重是 Channel<float>，先完成 `TrackFields.LayerWeights` 求值与合成，再读取本帧权重合成普通字段。`AnimationSystems.Add` 与 AddDefaults 共同进入这条阶段链，外部系统可按 AnimationPhase 加入。

单来源直接输出，多来源使用 `ComposeSystem<T,TOps>` 的同一值运算规则。每个最终字段在对应阶段产生一个 `FieldWrite<T>`。系统在对应阶段查询完成后汇总字段写入，再按共享 Channel 选择一个批次入口，经过一次 World.ExecuteComponentWrite 取得具体根组件批次，内部以具体 IFieldAccess 循环写值及推进版本。合成与字段访问分别按实际类型专门化。

## 验收

`AnimationLayersTests` 保留公开覆盖、叠加、权重、遮罩、外部贡献、自定义值和跨阶段行为。`TypedCompositionTests` 补充生成引用字段、固定来源顺序、持值权重变化、保持输出写版本、最新基准、动态来源归并与遮罩变化。固定来源、动态贡献、保持值及字段写回的性能证据分别记录于本轮队列报告。

[[architecture/animation/Property-Animation|Property Animation]] · [[architecture/animation/Animation-Graph|Animation Graph]]
