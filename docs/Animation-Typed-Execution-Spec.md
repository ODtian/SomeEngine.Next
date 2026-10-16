# 动画强类型执行与统一字段访问规格

状态：已确认（2026-10-16）。初稿日期：2026-10-15。任务票按本文第 17 节同步。

本规格沿 ADR 0014、动画票 #48–#52 与 ECS 写入票 #83 发展，明确属性动画的类型关系、数据持有、扩展接口和执行成本。属性动画与姿势动画继续共用时间线、值运算、层和图；属性值与骨骼数组分别采用适合自身的数据布局。

目标是让同一套动画服务桌面界面、游戏 HUD 和游戏数据：内置动画与用户动画使用相同公开接口；运行中的时间、参数和值保持动态；算法选择、字段访问和图关系尽量在声明、构图与结构修改阶段确定。

## 1. 既有基础与本次补充

| 既有基础 | 本次补充 |
| --- | --- |
| Timeline、阶段、事件计数，#48 | 明确算法的完成报告、批次时钟输入和状态持有 |
| 静态 IValueOps，#48 | 保留具体值运算类型，提供共同的标量和批量优化入口 |
| Curve 与 Spec，#48 | 用具体泛型类型表达算法、参数与实例状态，异构执行通过函数指针连接 |
| Channel、Track、内联与池化，#49 | 统一普通字段、嵌套值字段、引用对象内字段及可写属性 |
| 确定性层合成，#50 | 将来源、顺序、遮罩和输出关系按更新频率维护 |
| 图编译、融合与共享，#51 | 公开强类型算法叶节点与条件接入，编译阶段建立执行范围 |
| .Animate、基准值与呈现值、FLIP，#52 | 引用字段采用显式目标写入，生成访问函数贯穿提交与呈现 |
| ECS 字节与类型化写入，#83 | 字节路径与类型化字段路径共同遵循 ECS 写权限和版本规则 |

本文中的类型与方法签名确定公开能力和类型关系；任务票中的最终名称按项目命名规则收敛。完整实现需要同步受影响的 Wiki 原子页及 ADR 0014 的字段访问、分发范围表述。

## 2. 必须成立的设计规则

1. 组件根为现有 ECS struct，根内可以包含托管引用。
2. 属性动画的叶值继续使用现有 TValue : unmanaged 与 IValueOps<TValue>；组件根、参数和实例状态分别采用自己的约束。
3. 同一数据有一个主要持有者，派生关系按明确的更新点维护。
4. 内置与用户算法、值运算、条件、节点和系统使用相同公开扩展方式。
5. 非常低频且需要较强多态的扩展点允许虚方法和接口调用。
6. 高频动态选择使用 managed 函数指针，主要调用粒度为执行批次。
7. 热循环内保留具体类型，优先直接调用和泛型专门化，为内联和后续优化提供条件。
8. 属性动画在 Game world 写数据，经现有提取进入 Rendering world，再进入持久 RenderGraph 与 RHI。
9. 声明负责建立类型与容量；运行阶段主要修改已有数据、缓冲元素和启用位。
10. 开发检查集中在声明、构图、装载与显式结构修改阶段，沿引擎统一 validation 入口执行。

## 3. 数据模型与主要持有者

| 数据 | 内容 | 主要持有者 | 变化时机 |
| --- | --- | --- | --- |
| 通道声明 | 组件类型、字段身份、叶值类型、访问实现 | 生成的组件字段声明 | 编译和类型初始化 |
| Spec 参数 | 时长、阻尼、关键帧或路径数据引用等 | 直接播放的类型化参数数据，或共享图定义 | 创建、显式更新 |
| 算法代码 | 初始化、求值、重定目标、完成判断 | 具体求值器类型 | 构建应用时确定 |
| 算法状态 | 速度、积分历史、当前路径段等 | 动画实例的类型化 ECS 数据 | 初始化、求值、重定目标、回收 |
| 通用轨道值 | 起点、目标、本来源当前输出 | 轨道 | 启动、重定目标、采样 |
| 时间线 | 当前时间、阶段、速度、时钟、暂停状态 | Timeline | 时间推进与控制操作 |
| 图定义 | 参数定义、节点关系、输出范围、执行顺序 | 共享的编译图数据 | 编译和显式替换 |
| 图实例状态 | 活动状态、过渡进度、实例参数 | 图实例的 ECS 数据 | 图推进和参数写入 |
| 基准值 | 调用方期望的字段值 | BaseValue 及其既有提交入口 | 调用方提交 |
| 呈现值 | 合成完成后实际使用的字段值 | 目标组件字段 | 动画写回 |
| 执行批次 | 查询、类型化数据范围、具体执行入口 | 现有系统组合或图的执行数据 | 声明、构图与来源变化 |
| 临时贡献 | 当次求值产生的动态来源贡献 | 现有 Contribution 缓冲 | 当帧生成和消费 |

Spec、轨道当前值和算法状态分别表达参数、共同播放数据和算法历史。Spring 的速度放在 SpringState；Tween 使用自己所需的状态。算法额外需要的历史由自己的 TState 表达。

共享图中的只读参数保留在图定义中，实例保存定义引用与索引。直接 To 的参数由对应实例持有。实例显式覆盖图参数时，该实例的数据成为这份覆盖值的主要持有者。

## 4. Spec 的静态部分与动态部分

“静态”分别对应以下含义：

| 含义 | 本规格中的安排 |
| --- | --- |
| 值保持只读 | 参数以 readonly struct 和只读共享数据表达 |
| 多次调用复用 | 调用方可用 static readonly 保存常用 Spec |
| 代码类型确定 | 参数类型、状态类型、算法类型由泛型保留 |
| 关系预先确定 | 字段访问、来源顺序、图关系在声明或编译时建立 |
| AOT 已生成代码 | 应用声明过的具体组合在发布时生成对应入口 |

0.12 秒与 0.30 秒是同一 Tween 算法类型的两个参数值。不同刚度的 Spring 同样共用一个具体算法实现。运行中产生的参数可以进入相同的类型化入口。

Spec 的完整底层类型关系为：

```csharp
public readonly struct Spec<T, TOps, TParameters, TState, TEvaluator>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    public TParameters Parameters { get; }
}
```

公开工厂与生成代码负责推导这些类型，正常调用保留简洁写法：

```csharp
var hover = Spec.Tween<Color, ColorOps, CubicOut>(0.15f);
var press = Spec.Spring<float, FloatOps>(180f, 20f);
var path = Spec.Create<Vector2, Vector2Ops,
    PathParameters, PathState, PathEvaluator>(pathParameters);

PropertyAnimation.To(world, node, SurfaceChannels.Fill, hoveredColor, hover);
PropertyAnimation.To(world, node, PanelDataChannels.Opacity, 0.8f, press);
```

TParameters 与 TState 采用 struct 约束，允许包含托管引用。共享路径、关键帧和资源数据在求值期间按声明的只读方式访问；显式参数更新通过相同公开入口提交。

原有按 CurveKind 或 EasingType 输入的便利入口可以在声明或绑定时选择对应实现。具体类型与执行入口进入批次后，求值循环使用已经确定的算法。

原有任意标量函数指针曲线可以通过共同适配入口使用，其内部函数指针调用按每个样本计入成本。追求批量执行的扩展可以实现具体曲线类型，或提供包含完整循环的批次函数，采用相同的公共批次入口。

## 5. 公开算法接口

纯缓动函数通过公开的 IEasing 接口与 Tween<TEasing> 组合。有状态算法通过 IMotion 表达自己的状态、推进和完成语义。内置 Spring、关键帧与用户算法共同使用以下生命周期：

```csharp
public interface IEasing
{
    static abstract float Sample(float progress);
}

public interface IMotion<T, TOps, TParameters, TState>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
{
    static abstract MotionBoundary Describe(in TParameters parameters);

    static abstract void Initialize(
        in TParameters parameters,
        in MotionValues<T> values,
        ref TState state);

    static abstract MotionSample<T> Evaluate(
        in TParameters parameters,
        in MotionTime time,
        in MotionValues<T> values,
        ref TState state);

    static abstract void Retarget(
        in TParameters previous,
        in TParameters next,
        in MotionValues<T> values,
        ref TState state);

    static virtual void UpdateParameters(
        in TParameters previous,
        in TParameters next,
        ref TState state) { }

    static virtual bool ParametersEqual(
        in TParameters previous,
        in TParameters next) =>
        EqualityComparer<TParameters>.Default.Equals(previous, next);

    static virtual void Release(ref TState state) => state = default;

    static virtual void EvaluateBatch<TMotion>(
        ref MotionBatch<T, TParameters, TState> batch)
        where TMotion : struct, IMotion<T, TOps, TParameters, TState>
    {
        for (int index = 0; index < batch.Count; index++)
        {
            ref readonly TParameters parameters = ref batch.Parameters(index);
            ref TState state = ref batch.State(index);
            MotionTime time = batch.Time(index);
            MotionValues<T> values = batch.Values(index);
            MotionSample<T> sample = TMotion.Evaluate(
                in parameters, in time, in values, ref state);
            batch.SetSample(index, in sample);
        }
    }
}
```

| 输入或输出 | 具体含义 |
| --- | --- |
| MotionBoundary | Duration、Settled 或 External，以及编排所需的时长 |
| MotionValues<T> | 起点、目标、本来源上一次输出；由通用轨道持有 |
| MotionTime | 本次求值时间、距上次求值的增量；由时间线语义计算 |
| MotionSample<T> | 本次输出值与算法完成状态 |
| TState | 当前算法的实例历史，初始化后通过 ref 原地更新 |

Describe 在声明、编译或参数更新时调用，生成阶段编排所需的数据。Evaluate 的完成结果进入现有 Timeline 收尾规则。

Retarget 的输入值已经按第 10 节确定。算法据此延续自己的状态：Spring 保留速度，用户惯性算法保留自己的运动历史。UpdateParameters 处理参数更新所需的算法派生数据。Release 在实例回收或替换算法实现时处理算法状态。

参数相等通过 ParametersEqual，默认使用该参数类型的相等语义；内置参数与性能敏感的用户参数可通过相同入口提供直接比较。叶值相等使用 IValueOps<T> 的共同 Equals 入口，默认沿叶值类型的相等语义，并允许具体 TOps 提供直接实现。参数相同且目标相同的提交保留时间和状态。参数变化通过 UpdateParameters 生效；目标变化再执行 Retarget。

直接播放的时长更新同时更新其阶段描述。编译图中影响固定区间、输出数量或依赖关系的参数，通过显式重编译或图定义替换更新；只影响求值公式的参数可以沿类型化参数更新入口修改。

### 5.1 共同的批量优化入口

IMotion 同时提供带默认实现的 EvaluateBatch。默认实现对具体 TEvaluator 执行普通循环；内置和用户算法都可以提供自己的批量版本。

公开的 MotionBatch<T, TParameters, TState> 是限定在本次执行范围内的 ref struct，提供：

- Count：当前批次的元素数量。
- Parameters(int index)：返回 ref readonly TParameters。
- State(int index)：返回 ref TState。
- Values(int index)：返回该元素的 MotionValues<T>。
- Time(int index)：返回该元素的 MotionTime。
- SetSample(int index, in MotionSample<T> sample)：写入本次输出与完成状态。
- 在实际连续存储支持时取得对应切片的入口。

批次视图直接描述实际存储中的行或范围，数据布置可以采用现有 ECS 块和缓冲。批量接口的默认实现与专用实现使用相同数据；任何额外的数据整理成本都计入批次总成本。

每条动画的时间可以各自变化。批量接口同时支持各元素自己的时间，支持同步推进的一组值使用共享时间。

### 5.2 系统组合、数据声明与节点安装

三个阶段分别建立各自的数据：

| 阶段 | 公开入口形状 | 产生的结果 |
| --- | --- | --- |
| 系统组合 | AnimationSystems.Add(schedule, spec) | 对应具体类型组合的求值系统与执行入口 |
| World 中声明 | PropertyAnimation.Declare(world, entity, channel, spec) | 对应目标和实例所需的轨道、参数、状态与容量 |
| 图构建与实例化 | builder.To(target, channel, value, spec)，随后 Compile / Play | 类型化节点定义、安装入口与实例数据 |

Add 的泛型类型由 Spec 推导，组合阶段按实际使用的具体组合建立一次执行列表。AddDefaults 作为便利组合调用相同公开入口，用户自己的算法采用相同方式加入。

Timeline 推进、图推进、层权重求值与合成、普通字段求值与合成、图与时间线收尾，按现有阶段依赖组织。新增具体算法接入对应求值阶段；内置算法与用户算法共同受这些依赖约束。

声明负责 World 数据，系统组合负责调度，图实例化负责根据编译结果安装实例。三者通过具体类型和现有 ECS 数据连接，使用者可以分别组合公开能力。

## 6. 分发规则与调用频率

| 调用位置 | 方式 | 频率 |
| --- | --- | --- |
| 图构建扩展、编辑器、配置装载、系统创建和销毁 | 允许虚方法或接口调用 | 低频操作 |
| 类型已知的 To、By、参数更新 | 直接进入具体泛型实现 | 操作发生时 |
| 异构命令的启动、重定目标和回收 | 具体操作的 managed 函数指针 | 操作或命令批次发生时 |
| 动画求值入口 | 具体泛型批次函数的 managed 函数指针 | 每个实际批次一次 |
| 合成入口 | 具体值与运算组合的执行入口 | 每个合成批次一次 |
| 字段访问入口 | 生成或用户实现的具体批次函数 | 每个字段访问批次一次 |
| 自定义条件 | 具体条件入口；依赖允许时成批执行 | 活动候选条件检查时 |
| 批次内部 | 具体泛型调用和普通循环 | 每个值、轨道或元素 |

函数指针指向包含循环的执行函数。具体调用形状为：

```csharp
public unsafe struct AnimationBatch
{
    public QueryHandle Query;

    public delegate* managed<
        ref ImmediateSystemContext,
        QueryHandle,
        void> Evaluate;
}

batch.Evaluate =
    &TrackEvaluation<float, FloatOps,
        SpringParameters, SpringState<float>,
        SpringEvaluator<float, FloatOps>>.Evaluate;

batch.Evaluate(ref context, batch.Query);
```

声明或构图时确定函数入口。函数地址按具体实现共享；实例数据保留类型正确的 ECS 存储。代码地址与托管数据引用分别持有。

函数指针调用按间接调用计入成本。批次内部保留算法、参数、状态和值运算的具体类型，小函数具备直接调用和内联条件。最终以内联后的实际机器码、代码体积和批次成本验收。

拖拽、连续输入和响应式变化驱动的重定目标属于热路径。执行方式依据实际调用频率确定。

系统调度、查询回调和组件访问的动态调用一并计入完整路径。相关热入口沿 ECS 公共执行方式收敛；动画使用相同的系统与查询能力。低频生命周期接口继续按自身的多态需求设计。

## 7. 统一 Channel 与字段生成

通道统一表示“现有组件中的一个可写叶值”。身份为组件类型编号与该组件内生成的字段编号，叶值类型由 Channel<T> 保留。

```csharp
public readonly struct Channel<T> where T : unmanaged
{
    public ushort Component { get; }
    public uint Field { get; }
}

public interface IFieldAccess<TComponent, TValue>
    where TComponent : struct, IComponent
    where TValue : unmanaged
{
    static abstract TValue Read(in TComponent component);
    static abstract void Write(ref TComponent component, in TValue value);
}
```

生成器从字段和路径声明产生：

1. 字段身份枚举与 Channel<T> 常量声明。
2. 叶值类型、根组件类型和访问实现。
3. 字段路径的类型化读取、写入与必要的值类型回写。
4. 类型化目标提交与批次写入入口。
5. 适用于字节访问路径的偏移数据。

用户手写的 IFieldAccess 通过同一套声明进入生成结果，字段编号由生成器统一分配。字段身份用于进程内引用；持久数据沿引擎已有类型与数据映射方式表达。

内置缓动的便利工厂与已有枚举输入映射也从共同声明生成。用户新增算法通过具体类型接入同一套公开能力。

例子中的属性名称表示本规格约定的声明形状：

```csharp
[Channel("Style.CornerRadius")]
[Channel("Progress.Value")]
public partial struct PanelData : IComponent
{
    [Channel]
    public float Opacity;

    public PanelStyle Style;
    public State<float> Progress;
}

// 生成的统一入口：
PanelDataChannels.Opacity
PanelDataChannels.StyleCornerRadius
PanelDataChannels.ProgressValue
```

### 7.1 普通字段与值类型嵌套

对于可按字节访问的字段，生成声明取得并复用布局信息；执行数据保存已解析的访问方式和偏移。

字段定位使用 CLR 对该具体类型的实际布局。可常量折叠的访问在 NativeAOT 生成代码中确认。枚举字段编号和偏移的对应关系在声明或构图阶段解析。

嵌套值类型通过生成的直接访问或必要的局部值回写更新。可写属性使用属性提供的写入行为；计算属性可由用户 IFieldAccess 明确其写入语义。

### 7.2 引用类型嵌套

对于 PanelData.Style.CornerRadius，写入函数从当前组件取得 Style，再进行正常的 C# 字段或属性赋值。值类型和引用类型可以在同一路径中混合嵌套。

类型化访问函数在本次有效的 ECS 访问范围内取得引用，组件行和对象引用随当前数据解析。访问函数按字段路径和根组件类型共享，实例记录目标实体及数据关系。

同一可写叶值由一条主要组件路径作为动画目标。共享引用的其他使用者通过该组件或现有响应式叶值版本订阅变化。

### 7.3 字段编号与执行批次

Channel<T> 继续是紧凑的字段引用。字段描述、函数地址和路径代码按声明共享。

声明与构图建立读取和写入批次，把相同访问实现的数据交给同一个入口处理。批次内部使用已确定的偏移或具体类型访问。偏移路径、类型化字段路径共同服务相同的 Channel<T>、To、By、图和 .Animate。

## 8. ECS 写入与变化版本

字段写入沿现有 ECS 类型化或字节写入能力取得写权限，并推进对应根组件的行版本。表格和 sparse 存储采用各自已有入口。

引用对象内的叶值变化也推进根组件版本，使读取该组件的计算与提取系统观察到变化。State<T>.Value 等响应式叶值同时执行自身公开写入语义，依赖该叶值的计算观察其版本变化。

字段根组件版本表达组件使用者需要重新读取该数据；响应式叶值版本表达该共享状态的依赖变化。两类版本由各自现有数据持有，算法求值器提交值，字段写入层负责衔接对应变化路径。

查询的访问声明涵盖实际读写组件。Job 的适用性按数据中的托管引用、读写声明及 ECS 现有执行规则确定，内置和用户算法采用相同规则。

## 9. 声明、存储与放置

PropertyAnimation.Declare 显式声明通道、具体 Spec 类型组合及需要的容量。便捷声明可以一次列出界面状态会使用的 Tween、Spring 和用户算法。

```csharp
var fade = Spec.Tween<float, FloatOps, CubicOut>(0.15f);
PropertyAnimation.Declare(world, node, PanelDataChannels.Opacity, fade);
PropertyAnimation.Declare(world, node, PanelDataChannels.Opacity, press);
```

声明建立：

- Timeline 与共同轨道数据。
- 实际使用的参数和状态存储。
- 字段读取、写入及求值入口。
- 可复用的轨道、输出与缓冲容量。
- 该声明使用的来源、阶段和合成关系。

已有 Track<T> 的单目标内联、多目标融合和池化语义继续成立。共同轨道数据保持紧凑，具体算法的额外状态独立按实际类型存储。

内联容量按实际声明与状态体积选择，编译图按各具体组合的轨道数量预留容量。默认容量策略需要在交付中同时列出单通道、Tween 与 Spring 混用、较大自定义状态三组的保留内存。

已声明的类型集合与容量覆盖运行中的启动、切换、重定目标和停止。增加新的类型组合或扩大容量通过显式声明更新完成。

单目标热操作在已选定的内联实例内定位通道与层；返回的动画句柄用于后续实例控制。多目标图在构图与实例化阶段确定来源关系。沿既有池化路径首次放置的工作计入启动成本，并保留返回句柄供后续操作使用。

完成值按第 11 节保存；空闲的池化实例通过现有启用位和池化数据复用。

## 10. 播放、重定目标与时间语义

### 10.1 To、By 与重定目标

- To 的端点为起点到目标值。
- By 保留现有“位移衰减到启动基准值”的语义，调用方声明的位移由通用端点规则解释。
- 单来源首次启动从实际呈现值取起点。
- 已存在来源的重定目标，从该来源的上次输出继续；相同通道、相同层的替换保留当前输出连续性。
- 多来源合成时，各来源保存自己的输出，最终字段由合成结果决定。
- 同一种具体算法重定目标时，保留 TState 并调用 Retarget。
- 替换为另一种已经声明的算法时，回收原算法状态，以当前来源输出初始化新算法。
- 参数更新通过 UpdateParameters 处理；相同参数和目标的提交保持既有时间与状态。
- Stop、Pause、Resume、SetSpeed 使用现有时间线控制语义。

同一具体算法的重定目标行为由公开算法实现决定。内置 Spring 的实现保留速度，用户算法同样可以保留自己的历史。

### 10.2 时钟、阶段与完成

Game、Interface、Unscaled 沿现有 Timeline 与 AnimationTime 语义推进。每帧先计算各公共时钟增量，实例再应用自身速度、暂停与阶段时间。

求值算法报告自己的完成状态，通用求值入口汇入 Timeline.Running。该字段表达当前阶段仍有运行工作，所有求值来源按相同规则汇总。

Duration、Settled 与 External 继续沿现有阶段规则推进。固定时长阶段处理越界时间；收敛和外部阶段按现有边界语义进入下一阶段。事件继续通过计数提供给调用方。

Spring 的数值积分步长由算法决定，显示刷新率由宿主帧调度决定。100 Hz 或更高刷新率下使用实际帧增量，动画时间跟随所选时钟。

## 11. 来源、层与最终写回

基准值由调用方或 .Animate 的目标提交入口写入。算法提交本来源的采样结果，合成层决定最终字段值。

以下关系在声明、构图或来源变动时建立：

- 同字段输出范围。
- 来源与输出次序。
- 阶段内的覆盖与衔接关系。
- 层与遮罩的成员关系。
- 基准值索引。
- 单来源写回和多来源合成的执行方式。

共同合成规则继续采用 #50：按既有确定性次序执行覆盖或叠加，每个最终字段在对应写回阶段写入一次。单来源使用直接写回批次，多来源使用共同合成入口。

已经结束且需要保持的输出仍属于该来源。基准值、层权重、遮罩或来源关系改变时，相关输出参加重新合成。持续运行的轨道按帧求值，保持值按相关变化更新。

动态外部系统通过公开 Contribution 入口提交当帧贡献，使用相同的来源身份、次序、层、遮罩和字段写回规则。来源集合每帧变化的工作采用当帧批次，排序和整理成本计入完整求值。

## 12. 图编译、共享与公开扩展

现有构建器的 To、By、Delay、Sequence、Parallel、Stagger、Repeat、Branch、Boundary、StateMachine、Blend1D 与 Layer 继续作为图编排方式。

构建器的 To、By 接受完整的强类型 Spec。用户算法叶节点通过公开的类型化安装与声明入口接入，内置算法叶节点也使用此入口。

编译阶段建立固定时长区间、开放阶段、输出范围、来源顺序、状态到候选过渡的范围、Blend1D 采样点顺序和各实际算法组合的容量。

组合节点继续由调用构建器原语的普通函数表达。有自有状态的算法叶节点提供自己的参数、状态和完成描述，参加相同的阶段与融合规则。现有 Branch、内联冲突与容量拆分语义继续成立。

内置参数比较条件与用户条件采用相同的公开条件描述及执行入口。条件数据与函数入口在构图时确定，活动条件使用当前图参数求值。需要读取外部数据的条件声明自己的输入依赖。

条件的公开求值能力可表达为以下形状，参数保留在图定义中，状态保留在实例中：

```csharp
public interface IAnimationCondition<TParameters, TState>
    where TParameters : struct
    where TState : struct
{
    static abstract bool Matches(
        in TParameters parameters,
        in GraphParameters inputs,
        ref TState state);
}
```

内置比较条件同样提供具体参数、状态类型与 Matches 实现。类型化条件描述由公开工厂或生成代码建立，状态在实例初始化与回收时遵循共同的数据生命周期。

编译图由实例共享。实例保存当前状态与时间；拆分部分读取共同实例的时间与阶段，产生各自的运行结果，再由共同收尾阶段汇总。按执行范围读取必要数据，时间和状态各有一个主要持有者。

## 13. SomeUI 目标提交与呈现

### 13.1 .Animate 的共同入口

.Animate 接受与 PropertyAnimation、图构建器相同的强类型 Spec，使用 Interface 时钟。首次声明建立所需存储。

普通参数写入和生成的修饰器，经现有组件提交路径转换成字段目标。引用对象内的动画字段通过统一的字段目标写入口提交，例如：

```csharp
Scope.Set(node, PanelDataChannels.StyleCornerRadius, hovered ? 16f : 8f);
node.Animate(PanelDataChannels.StyleCornerRadius, radiusMotion);
```

Scope.Set 对所有 Channel<T> 采用同样的“提交目标值”语义。生成的修饰器也可以调用它。此入口在现有作用域的字段提交数据中保存目标，提交时更新基准值与轨道。

呈现字段由动画与合成阶段写入。引用对象使用同一个实际对象，目标值通过字段提交数据持有；组件副本中的引用继续遵循 C# 的引用语义。

相同目标的视图重跑保留动画进度。目标变化沿当前来源输出继续。现有异步构建、提交和恢复过程通过相同的字段读写入口保存与恢复字段目标。

### 13.2 响应式依赖与输入

hover、pressed 等交互状态由既有输入、命中测试和事件系统产生。视图或响应式计算读取这些状态，生成字段目标，随后由动画系统推进呈现值。

业务 State、ECS 组件依赖、基准目标和呈现字段分别表达自己的数据。动画每次写入沿第 8 节推进呈现数据的版本，使用者按实际读取来源登记依赖。

### 13.3 FLIP 与最终呈现

FLIP 继续比较同一实体布局前后的几何，通过 PresentationOffset 表达反向偏移与恢复过程。布局矩形、用户变换和呈现偏移按现有最终呈现规则组合。

动画只更新相应字段；布局、命中测试、裁剪与提取读取引擎规定的最终呈现数据。最终矩阵和透明度沿既有 Game world → Rendering world 提取路径送入材质数据，再交给持久 RenderGraph 与 RHI。

帧阶段沿现有统一引擎入口安装：视图目标提交、动画、布局、FLIP 与提取继续依照各自既有阶段衔接。本规格中的类型化与批次优化在这些阶段内实施。

## 14. 动态工作收敛表

| 当前需要处理的动态内容 | 提前确定或维护的部分 | 每帧及操作时仍需处理的数据 |
| --- | --- | --- |
| 算法种类、缓动种类 | 具体泛型实现与批次函数入口 | 参数、时间、当前值 |
| 自定义状态类型 | 声明时建立类型化存储 | 实例状态变化 |
| Spec 参数结构 | 具体参数类型、共享定义 | 新参数值与显式更新 |
| 字段身份 | 生成字段编号、类型与访问实现 | 实体与实际字段值 |
| 字段偏移 | 声明时解析一次 | 按已解析位置访问 |
| 引用对象内的字段路径 | 生成路径代码 | 当前对象引用与叶值 |
| 组件行位置 | ECS 当前查询与存储范围 | 结构变化后的当前行 |
| 初始值捕获 | 字段读取入口与端点规则 | 启动时实际值 |
| 重定目标 | 具体算法生命周期入口 | 新目标与已有算法状态 |
| 关键帧搜索 | 已排序数据及范围 | 当前时间所在区间，实例缓存当前段 |
| Spring 积分 | 具体公式、参数派生数据 | 增量、位移、速度和收敛 |
| 时钟选择 | 公共时钟增量 | 实例速度、暂停和阶段时间 |
| 轨道阶段覆盖关系 | 编译阶段建立输出范围与次序 | 当前阶段及活动输出 |
| 基准值寻找 | 声明或关系变化时解析索引 | 最新基准值 |
| 层次序、遮罩成员 | 层或遮罩修改时更新 | 权重和来源输出 |
| 贡献排序 | 固定来源在关系变化时排序 | 动态来源的当帧整理 |
| 图组与输出关联 | 编译输出范围 | 活动状态和混合权重 |
| 状态机候选关系 | 按状态建立过渡范围 | 当前候选条件结果 |
| Blend1D 点集合 | 编译时排序 | 当前坐标及相邻点 |
| 拆分图的共同时间 | 由共同实例持有 | 各部分的运行结果 |
| 同字段来源冲突 | 声明、构图和显式来源修改时处理 | 当前已确定实例内的操作 |
| 池化放置 | 声明与预留容量，复用已有查询 | 首次放置、取用与归还 |
| 已完成输出 | 保存保持值及相关关系 | 相关输入变化后的合成 |
| .Animate 的字段类型 | 生成类型化提交批次 | 本次目标与参数变化 |
| 校验 | 声明、构图和修改阶段建立不变量 | 功能所需的当前数据 |
| 代码选择 | AOT 生成已声明的具体组合 | 调用已经确定的函数入口 |

批次按实际算法、值类型、访问方式和数据依赖分别形成。求值按算法组合专门化，合成按值与运算专门化，写回按字段访问专门化。三者通过输出数据连接，代码体积按实际使用的组合统计。

## 15. 姿势动画的共同规则

姿势动画沿 #94 及其任务票采用整套姿势数组，片段采样按片段提供的具体数据和函数指针执行。片段装载、图实例化等低频扩展点可使用对象多态；逐帧采样、混合、层合成和后处理采用函数指针批次入口或具体类型调用。

姿势动画共享 Timeline、阶段、事件、图与层的语义，值运算的批量入口继续服务骨骼数组。属性通道与骨骼数组各自采用相应存储，用户节点与系统沿同一公开扩展原则接入。

本文对 #94 的补充范围为分发粒度、数据持有与扩展规则，片段编码、骨架和蒙皮行为继续由该规格及其任务票确定。

## 16. 验收与性能证据

### 16.1 公共行为验收

按 [Behavior Checks](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/wiki/harness/Behavior-Checks.md) 从公开调用端验证：

1. 普通字段、嵌套值字段、引用嵌套字段及可写属性，通过相同 Channel<T> 与播放 API 到达预期值。
2. 托管组件字段写入后，类型化读取、组件变化依赖与对应响应式叶值读取获得一致结果。
3. 自定义值运算、自定义缓动、有自有状态的算法，在 To、By、Retarget、图、层与 .Animate 中获得各自定义的结果。
4. 在独立消费者程序集中实现 Spring 与一个自定义状态算法，覆盖速度或历史延续、完成与复用。
5. 相同目标与参数的视图重跑保持动画进度，目标变化从对应来源的当前输出继续。
6. 既有阶段、时钟、Pause、Resume、Stop、速度与事件行为保持现有公开语义。
7. 层合成按给定公式和来源次序得到预期结果，单来源和多来源的最终字段值一致遵循该规则。
8. 自定义叶节点与条件进入现有 Sequence、Parallel、状态机和融合图后获得预期阶段结果。
9. NativeAOT 独立消费者通过相同公开入口运行上述代表性扩展。

每个行为用例表达一条可观察结果。性能、分配和代码生成证据单独记录。

### 16.2 动画成本基准

采用同一台机器、同一发布方式、固定时间序列与相同输入，分别记录：

| 基准 | 规模和内容 | 主要观察值 |
| --- | --- | --- |
| 纯批量求值 | 1,000 / 10,000 / 100,000 条持续活动轨道，Tween、Spring、自定义状态算法分别测量 | ns/轨道、CPU 中位数与 P95/P99 |
| 完整字段路径 | 普通字段、值嵌套、引用嵌套，包含求值、合成、写入与版本处理 | 完整 CPU 成本与分配 |
| 高频控制 | 持续重定目标，已声明算法间切换，批量启动与停止 | 操作成本及稳态分配 |
| 图执行 | 固定序列、含收敛阶段、状态机与混合 | 图推进与求值的分别成本 |
| 来源更新 | 固定来源与当帧动态来源分别测量 | 维护、排序与合成成本 |
| 生命周期 | 首次声明、首次播放、完成与复用 | 首次耗时、保留内存与峰值内存 |
| 扩展对等 | 内置实现与消费者中的同公式实现 | 同条件下的成本、分配和代码体积 |

纯求值基准同时给出处理相同参数、状态和时间的直接 C# 循环作为算法成本参照。完整路径包含调度、查询与组件访问，分别列出动画自身和这些公共层的成本。

CPU、GPU benchmark 通过 tools/Invoke-Benchmark.ps1 提交到公共 FIFO 队列。比较期间需要独占 CPU/GPU 的构建、内容导入和捕获采用同一队列。报告保留任务编号、结果目录、stdout、stderr 与退出码，执行方法沿 wiki/harness/Harness-Performance-Evidence.md。

稳态引擎执行路径的托管分配目标为 0 B/帧；首次声明和容量增长单独记录。自定义算法自身的分配独立归属，并以消费者中的零分配实现验证共同入口。

### 16.3 高频与内存目标

- 100 Hz 对应 10 ms 帧间隔，300 Hz 对应约 3.33 ms；HUD 极限场景以持续数百帧刷新作为整帧目标。
- 显示与帧调度跟随实际刷新或测量配置；动画采用实际时钟增量。
- 每次报告同时列出动画 CPU 成本、完整 CPU 帧时间、GPU 时间和实际呈现间隔。
- AOT 报告列出代码体积、首次声明成本，以及不同实际类型组合的实例与共享数据内存。
- 纯动画微基准用于解释动画成本；整帧与呈现指标用于评估 HUD 目标。
- 代表场景的具体动画子预算由 #52 在基准执行前记录并固定，性能报告采用同一预算与采样方式判定。

涉及渲染性能改动时，沿根级 AGENTS 使用 DefaultGame 统一场景平台，覆盖 1024 小球、Sponza 与 Amazon Bistro，通过 tools/Measure-Scenes.ps1 执行一次不超过 60 秒的多场景反馈。带 trace 的定位与普通性能采样分别记录。

### 16.4 最小代码生成核对

对代表性的 Tween、Spring、用户算法与引用字段写入，核对 Release / NativeAOT 机器码中的：

- 批次入口的间接调用位置。
- 热循环内部的算法和值运算调用形状。
- 关键小函数的内联情况。
- 字段定位与访问方式。
- 对应具体组合的代码体积。

函数指针按实际调用次数计入预算，内联收益以最终代码为准。AggressiveInlining 用于经核对有收益的小函数。

## 17. 任务票同步范围与依赖

本文作为现有动画需求的补充规格，任务票按以下边界同步：

| 现有票 | 补充内容 | 依赖 |
| --- | --- | --- |
| [#48](https://github.com/ODtian/SomeEngine.Next/issues/48) 核心 | 泛型 Spec、IMotion、批次入口、内置与用户对等、时钟与完成汇总 | 共同类型关系先确定 |
| [#83](https://github.com/ODtian/SomeEngine.Next/issues/83) ECS 写入 | 托管根组件的类型化字段访问、行版本与查询执行接入 | 与通道生成接口联合对齐 |
| [#49](https://github.com/ODtian/SomeEngine.Next/issues/49) 属性动画 | Channel 身份与生成、类型化状态、声明容量、放置与热控制路径 | #48、#83 |
| [#50](https://github.com/ODtian/SomeEngine.Next/issues/50) 合成 | 关系维护、字段访问批次、共同贡献入口与保持值 | #49 |
| [#51](https://github.com/ODtian/SomeEngine.Next/issues/51) 图 | 公开算法叶节点与条件、类型化数据、编译范围与共同状态 | #48–#50 |
| [#52](https://github.com/ODtian/SomeEngine.Next/issues/52) SomeUI | 强类型 .Animate、字段目标提交、引用字段、FLIP 接入 | #49–#51 与既有视图写入 |
| [#94](https://github.com/ODtian/SomeEngine.Next/issues/94) 姿势规格 | 共同分发粒度、值运算与图扩展规则 | 核心公开接入确定后同步相关票 |

字段生成实现归入引擎的共同生成能力，SomeUI 使用生成结果。若具体改动需要独立交付票，在对应原票下拆出可独立验收的任务，并引用本规格与其依赖。

## 18. 已确认的实现取舍

实现与验收遵循以下取舍：

1. **公开类型关系：** 底层完整保留 TValue、TOps、TParameters、TState、TEvaluator，上层由工厂和生成代码简化调用。
2. **字段身份：** Channel 使用组件编号与生成字段编号；偏移与类型化访问是声明解析后的具体执行方式。
3. **引用字段目标写入：** Scope.Set 与生成修饰器提交目标值，实际引用对象中的叶值由动画呈现阶段写入。
4. **来源持有与控制：** 运行中的热操作针对已声明或已经取得句柄的实例，来源关系在声明、构图与显式变化阶段处理。
5. **状态与容量策略：** 共同轨道保持紧凑，算法状态按实际类型存储；默认内联容量结合三组内存数据确定。
6. **高频预算：** 动画基准采用上述规模与完整路径范围，代表 HUD 场景的动画子预算由 #52 在执行基准前记录并固定。
7. **低频多态与热分发：** 生命周期和构建扩展按实际频率选择虚调用；异构热入口采用函数指针，热循环保留具体类型与内联机会。

## 19. 依据与源码对照

### 已确定设计与现有票

- [ADR 0014](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/docs/adr/0014-general-animation-core.md)
- [Animation](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/wiki/architecture/Animation.md)
- [Animation Extension](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/wiki/architecture/animation/Animation-Extension.md)
- [Property Animation](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/wiki/architecture/animation/Property-Animation.md)
- [Animation Layers](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/wiki/architecture/animation/Animation-Layers.md)
- [Animation Graph](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/wiki/architecture/animation/Animation-Graph.md)
- [SomeUI Animation](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/wiki/architecture/someui/Animation.md)
- #48–#52、#83 与 #94 的正文和讨论；票的范围见第 17 节。

### 确认规格时的源码基线

以下源码对照记录确认本规格时的基线；实施后的公开入口与执行证据见各原子 Wiki 和
[本次验收记录](../wiki/evidence/Animation/2026-10-16/Typed-Execution.md)。

- [Spec 与 Curve](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.Animation/Spec.cs)：当前 Spec 持有 Duration 与 Curve。
- [Curve](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.Animation/Curve.cs)：当前按种类求值、标量函数指针与 Spring 推进。
- [IValueOps](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.Animation/IValueOps.cs)：现有静态值运算与批量默认方法。
- [TrackSystem](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.Animation/TrackSystem.cs)：当前轨道求值与 Spring 专门分支。
- [PropertyAnimation](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.Animation/PropertyAnimation.cs)：当前声明、放置与重定目标。
- [ComposeSystem](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.Animation/ComposeSystem.cs)：当前来源排序、合成与字段写回。
- [AnimationBuilder](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.Animation/AnimationBuilder.cs)、[CompiledGraph](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.Animation/CompiledGraph.cs)：当前图声明与安装函数指针。
- [AnimatedField](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.Views/AnimatedField.cs)、[ComponentCopy](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.Views/ComponentCopy.cs)：当前字段提交函数与组件副本。
- [DynamicBuffer](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.ECS/DynamicBuffer.cs)、[ComponentMetadata](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.ECS/Registry/ComponentMetadata.cs)：现有 struct 数据与托管引用处理。
- [ComponentOperations](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.ECS/Registry/ComponentOperations.cs)：组件操作函数指针和类型化复制。
- [SystemNode](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.ECS.Systems/SystemNode.cs)、[World.Queries](https://github.com/ODtian/SomeEngine.Next/blob/codex/someui/src/SomeEngine.ECS/World.Queries.cs)：当前系统虚调用和查询回调入口。

### 代码生成参考

- [C# 函数指针](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code#function-pointers)
- [MethodImplOptions](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.methodimploptions?view=net-10.0)
- [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)



