# Property Animation

属性动画以 `Channel<T>` 定位组件字段，叶值为 unmanaged，组件根为 ECS struct，根内可持有对象引用。时间线、来源输出和具体算法状态分别持有自己的数据。

```csharp
[GenerateFields]
[ComponentField("Style.Corner.Radius", Name = "Radius")]
[ComponentField(typeof(CustomOpacity), "Custom")]
public struct PanelData : IComponent
{
    public float Opacity;
    public Style Style;
    public float Width { get; set; }
}

var fade = Spec.Tween<float, FloatOps, CubicOut>(0.15f);
var spring = Spec.Spring<float, FloatOps>(180f, 20f);
PropertyAnimation.Declare(world, node, PanelDataChannels.Radius, fade);
PropertyAnimation.Declare(world, node, PanelDataChannels.Radius, spring);
Entity animation = PropertyAnimation.To(world, node, PanelDataChannels.Radius, 16f, spring);
PropertyAnimation.Retarget(world, animation, PanelDataChannels.Radius, 8f);
```

## 字段声明

共同的 ECS 生成器为 `[GenerateFields]` 组件生成 `PanelDataFields` 字段身份枚举、`PanelDataFieldAccess` 类型化访问和 `PanelDataChannels` 通道。struct 与 record struct 的普通公开字段及可写属性自动加入，`ComponentField` 补充嵌套路径与手写 `IFieldAccess<TComponent,TValue>`。同一声明中的编号从 1 自动分配。引用 Views 的消费者还获得 `PanelDataFieldTargets.SetRadius(node,value)` 等目标修饰器，经 `Scope.Set` 提交目标。

`Channel<T>` 的身份是 `Component + Field`，叶值类型由 T 保留。通道保存共享访问描述的索引；读取、写入、组件副本访问与批次函数地址由声明共享。可直接按 CLR 布局访问的字段路径在类型初始化时取得实际偏移，后续复用该偏移。嵌套值属性通过具体 C# 路径读取并回写中间值；引用路径在有效 ECS 访问范围内取得当前对象。

```csharp
T value = channel.Read(world, node);
channel.Write(world, node, in value);
T submitted = channel.ReadRoot(in componentCopy);
channel.WriteRoot(ref componentCopy, in submitted);
bool throughReference = channel.ThroughReference;
```

`ThroughReference` 表达组件副本与叶值之间的引用路径；手写访问默认采用该语义。Views 使用独立字段目标提交保存引用叶值。旧的 `Channel<T>.Of<TComponent>(FieldRef)` 继续提供原有偏移字段便利入口。

## 声明与数据

完整的 `Spec<T,TOps,TParameters,TState,TEvaluator>` 保留到 `MotionInstance<T,TOps,TParameters,TState,TEvaluator>` 的实际 ECS 缓冲与 `TypedTrackSystem`。参数和状态采用 struct 约束，可包含托管引用。`MotionInstance` 的唯一 Data 字段保持连续的具体参数、状态、输入与采样结果，求值批次直接借用这段存储。

`Declare(world,target,channel,spec,capacity,layer)` 建立目标内联时间线、共同轨道、具体算法容量、来源关系及生命周期入口。同一通道声明多个算法时共用通用轨道，各具体算法保有自己的类型化存储；多个通道的声明容量累计进入实际组合。具体算法状态默认内联 1 个元素，调用方按并发来源数声明容量。完整 Spec 仅放置实际声明的组合；旧的 Declare<T> 预放置 Linear Tween、函数 Tween、Spring 与 Keyframes 四种内置组合，旧 Spec 在绑定时选择对应具体算法。

目标上的 `PropertySource<T>` 是来源关系的主要持有者：记录通道、层、动画实体与输出轨道。声明实际追加关系时及显式 `RefreshSources<T>` 更新确定性顺序、基准值索引、遮罩成员与直接/合成方式，并把索引写回轨道。相同声明保持已有关系及保持值的呈现写版本。运行中的 To 在当前目标的已解析来源范围内定位。完成实例通过现有 ECS 池复用，复用时释放原算法状态并重置时钟、速度、暂停与阶段数据。已播放来源转入后来声明的目标内联存储时，具体状态随来源移动，Spring 延续原有速度。首次放置与容量增长属于启动成本。

通用轨道持有目标、端点、本来源 Current、层、阶段、具体状态索引与来源索引。Spring 的速度等算法历史存于相应 TState。来源缓存中的输出是供合成读取的派生值，主要采样值由轨道持有。

## 控制和生命周期

- To 从本来源当前输出继续；新来源从字段或叠加层基准捕获起点。By 从启动基准加位移开始，衰减回启动基准，叠加层保持围绕最新基准的位移。
- 同算法改目标调用 Retarget，保留状态；参数变化调用 UpdateParameters。只有参数变化时保留原始时间与端点；相同参数和目标保留进度。叶值比较采用 TOps.Equals。
- 已声明算法切换时，从当前来源输出初始化新状态，并调用原算法 Release。完成保留来源输出与算法历史，下一次同算法目标变化可继续历史；Stop、切换与池复用按各自回收点释放状态。
- Retarget 使用已取得动画实体上的活动通道。Pause、Resume、SetSpeed 继续操作同一时间线。Stop 保留呈现值并禁用时间线。
- Timeline 是时间、时钟、暂停、速度与事件计数的主要持有者。直接播放把 Spec.Boundary 写入当前 TimelineStage，并保留已有后续阶段。同阶段来源按固定时长、Settled 与 External 共同规则汇总；External 通过 Animation.Advance 离开。具体算法报告 MotionSample.Finished，共同收尾阶段汇总完成。

## 执行与写回

`AnimationSystems.Add(schedule,spec)` 进入共同阶段，时间线与图推进后先求值和合成层权重，再处理普通字段，最后统一收尾。查询执行入口使用 managed 函数指针，批内保留具体算法与值运算类型。

单来源采用直接写回批次；固定多来源及分层值由 [[architecture/animation/Animation-Layers|Animation Layers]] 合成。`FieldWrite<T>` 保存最终字段值，系统在对应阶段查询完成后汇总相同 Channel。`Channel.WriteBatch` 经一次 `World.ExecuteComponentWrite` 取得具体根组件批次，再以具体 IFieldAccess 循环写入。写入沿 [[architecture/ECS/Component-Fields|Component Fields]] 推进根组件版本，table 与 sparse 根共享调用形状。

## 验收

`GeneratedChannelTests` 覆盖普通字段、值嵌套、引用嵌套、属性与手写访问。`TypedPropertyAnimationTests` 覆盖完整 Spec、参数/目标控制、Spring 速度、含引用的用户状态、声明后 100 次控制与池复用。`TypedCompositionTests` 覆盖固定关系、持值及共同写回。成本、容量内存、NativeAOT 与机器码证据单独形成性能记录。
