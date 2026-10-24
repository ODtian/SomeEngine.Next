# Pose Animation

## Definition
姿势动画以整套骨骼姿势为数据单位，是引擎动画子系统的数据形态。它与属性动画共用时间线、层与混合规则、图的构建与融合；骨架、压缩片段、姿势缓冲、按骨骼遮罩混合、IK 与物理控制、根运动和蒙皮输出由它自己负责。

## Current design
- 状态：骨架资产、GLB skin 导入、原始片段采样、骨架实例、共同图播放和 Cluster inline 蒙皮已实现。规格见 `docs/Pose-Animation-Spec.md`。
- 骨架数据、公开 API、关节映射与 GLB 层级规则见 [[architecture/animation/Skeleton-Assets|Skeleton Assets]]。
- 混合使用 `IValueOps<T>` 的批量版本，按骨骼数组做 SIMD。
- 蒙皮输出作为一种 Deform 实现接入 cluster 渲染（[[architecture/cluster/Deform-Cache|Deform Cache]]）。
- 骨骼通过既有提取进入 Rendering world，当前与上一帧 G 按产生结果的实例区段上传；应用回调、实例绑定和用户编码接口见 [Skinning](Skinning.md)。
- 每种片段方案由 Importer 与 `AnimationClipBlock` 子类组成，来源 `.meta` 选择具体组合。`RawClipBlock`、`AclClipBlock` 和用户 Block 经同一 `Sampler` 提供采样函数及共享数据；公共 glTF 曲线读取由普通库函数提供。
- 原始片段通过 `RawClipImporter` 与 `RawClipBlock` 接入；ACL 压缩片段通过 `AclClipImporter` 与 `AclClipBlock` 接入，导入设置持有采样率与误差参数。两者共用 `AnimationClip.SampleLocal`、批量采样和 `ClipLeaf` 播放入口；来源插值及用户编码扩展见 [Clip Assets](Clip-Assets.md)，压缩与原生分发见 [ACL Clips](ACL-Clips.md)。
- 片段通过共同 Leaf、Timeline 和 Composition 播放，具名结果 buffer 支持用户节点交接、事件与根运动；公开调用见 [Pose Graphs](Pose-Graphs.md)。
- 权重混合通过 `BoneTransformOps` 的具体批量运算和 `PoseBlendLeaf` 接入；1D/2D Blend Space 保存插值范围和片段相位，数据与调用见 [Pose Blending](Pose-Blending.md)。
- 动作状态、用户条件与交叉淡入沿共同 `StateMachine` 和姿势结果组合，调用与数据归属见 [Pose State Machines](Pose-State-Machines.md)。
- 逐骨 Override 与参考姿势 Additive 使用就绪 `BoneMask` 和共同层权重，Layer、Blend 与 Warp 按图内结果依赖求值；资源与调用见 [Pose Layers](Pose-Layers.md)。
- 骨骼按数组存储，挂点按需成为实体。`PoseStorage` 持有连续的局部姿势和模型空间 G，`SkeletonInstance` 通过 `PoseRange` 引用自身区段；对应求值阶段通过公开 `Update` 写入结果。
- 挂点通过实时骨骼视图、调用方偏移和普通角色父关系更新实体变换；绑定、消费顺序与释放规则见 [Bone Attachments](Bone-Attachments.md)。
- `SkeletonInstance.ModelPose` 提供只读模型空间 G、求值版本和计算时刻；创建、更新与使用期见 [[architecture/animation/Skeleton-Instances|Skeleton Instances]]。
- 单父骨架继承通过独立创建或挂接直接共享父实例 G，模型区段分别保存计算时刻与采样时刻；连接、拆卸和蒙皮绑定见 [Skeleton Inheritance](Skeleton-Inheritance.md)。
- 根运动输出 `RootMotionDelta`，由移动系统决定如何应用。
- Motion Warping 通过共享来源轨迹与窗口、逐角色目标输入修正根运动，查询与播放共用累计轨迹，见 [Motion Warping](Motion-Warping.md)。
- 选定片段、序列与带曲线的混合/遮罩路径通过显式时间基准批量预测根和骨骼，结果带播放条件与计算时刻，见 [Pose Prediction](Pose-Prediction.md)。
- 根、脚跟和脚尖轨迹与四类接触区段通过导入分析和动作标记取得，已选定路径提供下一接触与预测窗口，见 [Foot Motion](Foot-Motion.md)。
- FABRIK 通过共同叶节点消费来源姿势，按实例目标进行定长链投影与关节圆锥限制，见 [FABRIK](FABRIK.md)。
- PBIK 将骨盆、躯干和四肢放在同一全身约束求解中，按多末端目标、骨骼刚度、优先角度与关节限制生成姿势，见 [PBIK](PBIK.md)。
- GPU 状态机共享紧凑状态与过渡表，各实例独立推进播放时间和交叉过渡，实际播放结果随 G 一起读回，见 [GPU State Machines](GPU-State-Machines.md)。
- 选定 GPU 骨骼范围通过正常提取异步交付，保留实例、启动及输入身份；共同叶节点消费匹配反馈完成阶段，见 [GPU Pose Readback](GPU-Pose-Readback.md)。
- GPU Blend Space 和逐骨层复用共享坐标、相位及遮罩资源，在 GPU 合成局部 TRS 后写入同一模型空间 G，见 [GPU Blending](GPU-Blending.md)。
- GPU 解析双骨骼和 Aim 按共同模型空间目标调整来源 G，将骨盆平移、角度限制、权重与后代传播连接到相同历史和 Cluster 资源，见 [GPU IK](GPU-IK.md)。
- CPU 同步需求展开祖先和模型算法依赖，在共同更新的 Completion 点取得关键骨骼，再经普通提取把所写 G 范围交给 GPU 完成剩余姿势；逐骨骼时间与显示依赖见 [CPU/GPU Pose Handoff](CPU-GPU-Pose-Handoff.md)。

## Non-goals
- 不把每根骨骼表示为一条属性动画轨道。
- 推迟：物理控制与需要物理查询的 IK、骨架间重定向、可编辑的图资产、基于姿势的游戏功能。

## 共同执行规则

2026-10-16 确认的[完整规格](../../../docs/Animation-Typed-Execution-Spec.md)规定属性与姿势共用 Timeline、阶段、事件、静态值运算、层及公开图扩展。`Spec<T, TOps, TParameters, TState, TEvaluator>` 保留具体算法类型，参数和状态可持有托管引用，姿势叶节点通过共同公开声明和安装能力接入。

片段参数、压缩数据和采样入口由共享资产持有；节点关系、输出范围、候选过渡和混合点由编译图持有；时间、图输入、活动状态及算法历史由实例持有。中间姿势沿 Job 线程复用存储，最终姿势及矩阵保持在连续姿势存储中。

Block 属性、图配置及实例化采用低频对象多态。逐帧采样、混合、层和后处理以 managed 函数指针进入包含完整数组循环的具体批次，循环内部保留类型、SIMD 和内联机会。图编译及显式关系修改建立来源与输出次序，每帧处理时间、活动状态和权重。层权重通过生成通道与强类型 Spec 接入属性动画。

顺序为时间线、图与层、后处理、层级传播、蒙皮矩阵、提取。求值来源向共同时间线提交完成状态；事件以计数公开，根运动交给移动系统应用。数据沿 Game world → Rendering world → 持久 RenderGraph → RHI 提取。

## 子票分工

| 子票 | 公开能力与数据 |
| --- | --- |
| #96、#102 | 片段 Block、共享压缩数据、Sampler 与完整骨骼数组采样 |
| #98 | 公开叶节点、片段实例状态、时间、事件和根运动 |
| #99 | 静态值运算、骨骼数组批量混合与 Job 同步 |
| #100 | 内置及用户条件、候选过渡范围和独立实例状态 |
| #101 | 共同层权重、来源关系与姿势合成 |
| #104 | 双骨骼 IK、注视及用户后处理的公开组合与权重 |
| #109 | 纯算法及完整公共执行路径的队列基准、分配、内存与代码生成 |
| #110 | 示例与 NativeAOT 消费者实际执行公开扩展 |

CPU/GPU benchmark 和比较期间的独占构建、导入、捕获通过 `tools/Invoke-Benchmark.ps1` 公共 FIFO 队列，记录任务编号、结果目录、日志及退出码。验收以公开姿势、版本、事件及实例行为为准；性能证据独立记录算法、公共层、首次成本、稳态分配和代码体积。

## Relations
[[architecture/animation/Animation-Layers|Animation Layers]] · [[architecture/animation/Animation-Graph|Animation Graph]]
