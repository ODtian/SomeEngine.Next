# FABRIK

`FabrikChain` 保存链的骨骼编号、关节限制、位置容差和迭代次数。`FabrikLeaf` 将它接入共同图，`FabrikInstance.Target` 保存各实例的模型空间目标。具体计算使用 FABRIK 的往返定长投影，并在每步投影中应用方向限制；算法来源见 [FABRIK 原论文](https://www.andreasaristidou.com/publications/papers/FABRIK.pdf)及[关节约束扩展](https://doi.org/10.1002/cav.1630)。

## 数据与求解

链按父到子列出直接相连的骨骼，编号对应 `Skeleton.Parents` 的自有骨骼索引，最后一个骨骼为末端。`FabrikJointLimit` 指定某条骨段起始骨骼的圆锥轴及最大夹角：轴位于该骨骼的父坐标系，角度使用弧度。

每次更新从输入节点的局部姿势组成模型矩阵，记录链起点及当前骨段长度。求解先将末端放到目标，从末端向起点投影，再恢复起点并向末端投影；两次遍历都约束方向并复用记录的骨段长度。末端进入容差后结束当前求解，迭代次数限制每次计算的工作量。

求解结果通过实际父模型矩阵恢复各关节的局部旋转，来源的局部平移、缩放和其余骨骼姿势继续保留。输出交回普通 `PoseCompositionSystem`，由最终 FK 生成公开模型空间姿势。

## 共同图调用

下面是三段链的调用示例。来源结果为 `0`，求解结果为 `1`，目标实体通过 `PoseAnimation.CreateTarget` 分配两个结果并选择结果 `1`。

```csharp
var chain = new FabrikChain(skeleton, [1, 2, 3, 4],
    [new FabrikJointLimit(1, Vector3.UnitX, MathF.PI / 6)],
    tolerance: .001f, iterations: 32);

using (builder.Parallel())
{
    builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null,
        new(clip, new PoseResultId(0), Loop: true));
    builder.SolveFabrik(Entity.Null,
        new(chain, new PoseResultId(0), new PoseResultId(1)));
}
using CompiledGraph graph = builder.Compile();
Entity animation = graph.Play(world, target);
Vector3 goal = new(1, 5, 0);
world.ExecuteBufferWrite<FabrikInstance, Vector3>(animation, ref goal,
    static (instances, ref goal) => instances[0].Target = goal);

PoseAnimation.AddSystems(systems,
    nodes: static nodes => nodes.Add(new FabrikSystem()));
```

同一编译图可以播放到多个目标。每个安装实例分别持有目标、工作数组和结果引用；运行中更新 `Target` 后，下一次活动来源求值采用新目标。

`RegisterNode` 保存输入输出连接，共同依赖级别决定求解与其他节点的次序。`FabrikSystem` 在对应级别提交具体链求解 Job，声明共享定义读取和实例工作区写入，等待这些 Job 完成后写入姿势结果。输出保留来源的时间、相位、启动标记与根运动，贡献权重沿共同图和动画层提供。

源码：[链定义与投影](../../../src/SomeEngine.Animation.Pose/FabrikChain.cs)、[叶节点与实例](../../../src/SomeEngine.Animation.Pose/FabrikLeaf.cs)、[具体求解系统](../../../src/SomeEngine.Animation.Pose/FabrikSystem.cs)。
