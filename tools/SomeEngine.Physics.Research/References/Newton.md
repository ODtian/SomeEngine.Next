# AVBD 刚体模型、Newton 来源与阶段归属

默认 `RigidConstraints` 使用硬单边接触和硬关节，逐刚体进行 3×3／6×6 块下降，并在同一主循环中更新对偶。默认帧配置为一个子步、八轮基础迭代；二维和三维硬接触按实际 SAT 残差续算至多八倍基础轮数，并记录全部实际工作。`CompliantMaterials` 显式选择 Newton v1.7.0 的有限材料 ALM。来源版本由 [Sources.json](Sources.json) 固定。

| 对比要素 | RigidConstraints | CompliantMaterials |
| --- | --- | --- |
| 接触模型 | 几何间隙与法向压力互补，库仑摩擦 | 有限材料 K，间隙加压力/K 与压力互补，库仑摩擦 |
| 关节模型 | 硬锚点重合和角锁 | 平移、转动反力分别由有限 K 定义 |
| 原始行 | Giles 硬关节与配套接触；二维及三维自由接触采用 Newton 共享 ρ 和对偶；三维自由接触采用 Newton legacy hard 的正度量 | Newton compliant ALM，材料 K 与数值 ρ 分别持有 |
| 数值历史 | Newton 最新锚点与反力转运；二维按局部见证点匹配，三维按世界中点匹配 | Newton 最新锚点、世界中点匹配与反力转运；按配置保留历史 |
| 刚体调度 | Giles 初始化及对偶阶段；三维结构分量分色装配和提交；大型自由分量按岛并行，支撑状态选择载荷传播次序 | Newton 材料流程，本地 CPU 顺序 GS |
| 长程距离界 | 硬连接长度导出及显式输入 | 显式 DistanceRanges 输入 |
| 双精度参考 | OfflineDynamics.py、ContactEquations.py 的全局硬约束 SQP/KKT | NewtonReference.py 的有限材料方程，CheckNewton.py 按输入离散独立求解 |

有限材料默认接触 K=10⁷ N/m、关节平移 K=10⁵ N/m、关节转动 K=10⁵ N·m/rad、接近阻尼 0 N·s/m。关节材料值沿用 Newton 默认值，接触值归项目盒体材料。刚性输入导出硬约束模型；材料 K 和数值罚参数 ρ 分别持有。

| 阶段 | 来源 | 本地位置 | 状态与工作归属 |
| --- | --- | --- | --- |
| 刚性初始化、主迭代、硬关节与对偶 | Giles solver.cpp、joint.cpp、manifold.cpp | CMake 生成的求解器、BodyEquation.cpp、AvbdContact.cpp | 原始 AVBD 循环持有刚体更新和乘子更新；关节块保持原角向曲率的列范数界 |
| 三维自由角向预测、惯性和速度 | Newton forward_step_rigid_bodies、solve_rigid_body、spatial.py | NewtonRigid.cpp、StepVelocity.cpp | 体坐标陀螺项、旋转后的世界惯量及对数旋转差；进入同一次刚体分解与阶段末速度重建 |
| 三维硬关节配套离散 | Giles 关节行、四元数差与角向惯性；项目拓扑选择 | researchStructuralBody、researchNewtonContact | 关节相邻流形保留 Giles 预测、惯性、接触势能和对偶；几何锚点与反力匹配采用 Newton 适配 |
| 硬关节初值 | 项目相对姿势与点响应适配 | researchInitializeJoint、researchJointRotationError | 角锁保持创建时相对转动；平移和角向各行由时间步、质量、惯量和力臂初始化数值罚参数 |
| 三维结构分量分色 | Newton GREEDY 分色及同色装配后统一提交；项目冲突图适配 | researchSolveRigidColors | 关节、接触与 LRC 距离边定义冲突；与关节或距离界连接的动态分量分色；各独立岛保留同色计算后统一提交的顺序；拓扑版本使颜色失效 |
| 三维类型化约束邻接 | Newton RigidForceElementAdjacencyInfo；项目 CPU 有序数组 | BodyForce、researchBodyEquation | 保持原力链表次序；拓扑变化时分类接触、关节与其他力；主循环直接读取类型化指针，其他力调用各自虚函数 |
| 二维接触初始化、块和对偶 | Newton 锚点及硬接触分支；Giles 盒体裁剪；项目局部见证点匹配 | initializeNewtonContact、researchContactEquation、researchUpdateContactPressure | 预测后几何刷新与跨步热启动分别处理；刚性接触采用对称局部见证点距离、一对一匹配与法向门限；法向和切向共用当前 ρ，投影后按原始穿透增长 ρ；LRC 反力随同一匹配转运；同一次 3×3 分解 |
| 三维自由接触残差与对偶 | Newton evaluate_rigid_contact_from_collision、update_duals_body_body_contacts | AvbdContact.cpp | 切向残差采用实际接触点位移和稳定化项；三轴共享 ρ；总力投影到库仑圆盘，随后按原始穿透增长 ρ |
| 三维自由接触度量 | Newton evaluate_rigid_contact_from_collision 的 legacy hard 分支；项目 CPU 代数装配 | researchContactEquation | 法向 Kₙ=ρnnᵀ，切向 Kₜ=ρ(I−nnᵀ)；两项同时激活时直接装配 ρI 对应的 JᵀKJ；其余激活组合沿用逐方向装配，进入原 6×6 分解 |
| 局部非线性求解与分解 | Giles 正角向曲率界；项目块内细化与数值精度 | researchContactEquation、solveBodyBlock、BodySolve | 三维自由接触加入正角向曲率界；表面更新超过 1 mm 时至多四次局部细化；6×6 LDL 使用双精度中间值，零角更新保留原转动 |
| 接触数值稳定化参数 | Newton 分类 α 和硬接触 α·γ 保留；取值归项目 | rigidContactAlpha、initializeNewtonContact、beginLongRangeStep | 二维接触和三维自由接触采用 Newton legacy hard 默认 α=0.95，同时用于法向稳定化、切向残差及反力保留；三维关节及配套接触沿用 Giles α=0.99；二维 γ=0.99，三维 γ=0.999 |
| 三维几何点适配 | Newton collide.py::write_contact；项目初始几何界 | initializeNewtonContact | 世界中点加法向半间隙构造点对，再转体坐标；关节接触 C0 法向由初始双盒 SAT 穿透限制；自由接触在初始 SAT 分离时将法向 C0 设为零，重叠时从当前面见证点形成 C0 |
| 接触特征完整性 | Newton 面积约简；项目最深点保留和几何重建 | reduceNewtonContactPatch、researchContactPatch、researchRefreshContacts、researchRefreshPlanarContact | 实际穿透时保留完整裁剪多边形的最深见证点，并保持其余点的面积覆盖；SAT 与现有行不一致时刷新材料点；二维在几何残差续算前刷新，三维在每轮开始时刷新；空点集在同一步重新检测 |
| 三维接触旧穿透偏置 | Aniysk/PhysX_AVBD b46e2eed 的 computeC0 深度自适应公式；项目精度参数 | initializeNewtonContact | C0 的法向稳定化偏置随旧穿透深度线性衰减；上游阈值为 50／200 mm，项目按碰撞精度选取 1／5 mm；切向 C0 和乘子保留遵循 Newton 硬接触，全部进入原始接触行 |
| 硬接触的旧误差保留 | 项目 AVBD 数值配置 | researchContactNormalStart、researchContactEquation、researchPairedContactEquation、接触对偶 | 三维自由和结构接触将继承的法向误差限制在 constraintAccuracy 的四分之一，即 0.25 mm；原始块和对偶使用相同残差 |
| 约束运动后的候选检测 | 项目运动包络与查询阶段 | researchBeginCollisionMotion、researchAddMovedContacts | 刚性主循环用步初位姿记录预测位移，并在首次原始块之前补建接触；约束更新越过运动阈值时重新检测候选，并更新同一组接触与物理岛 |
| 三维数值历史 | Newton contact_match.py、_refresh_rigid_contact_state；项目数值 ρ 生命周期适配 | initializeNewtonContact | 最近中点、法向门限 0.995、位置门限 0.5 mm、一对一匹配持有接触及 LRC 反力；关节接触逐轴 ρ 在同法向流形内保留最大值，跨步衰减一次；反力与数值罚参数分别持有历史 |
| 几何刷新时机 | Newton PRE_INIT 默认频率及预测后刷新配置；项目结构接触适配 | researchRefreshContacts | 自由接触在积分前构造接触帧，主循环持有同一锚点及 C0；关节接触首轮按相对切向移动或转动的 10 μm 阈值刷新，初始几何界、反力和支撑图一同更新 |
| 盒体候选和窄相 | Giles 盒体裁剪、项目扫掠剪枝、Newton shape gap | Islands.cpp、Bridge.cpp、initializeNewtonContact | 二维硬行及三维关节宽相沿用运动包络；三维自由宽相每形状 gap=0.1 m；三维自由初始化窄相形状对 gap=0.2 m；结构接触采用项目每形状 gap=0.01 m，形状对 gap=0.02 m；表面 margin=0；硬接触先筛选几何，再创建流形，点集由该流形交给初始化 |
| 盒体间隙检查 | 项目分离轴查询 | researchContactPenetrates | 先用现有单位法向投影完整盒体；该投影满足间隙精度时结束检查，其余情况查询全部面轴与叉积轴 |
| 三维四点边界约简 | Newton multicontact.py::approx_max_quadrilateral_area_with_calipers，源自 Jitter Physics 2 | reduceNewtonContactPatch | 旋转卡壳、直径及两侧最大面积选择，上游相对平局阈值 0.1%；三维结构接触及零重力自由接触保留至多四个面内边界点；有重力的自由接触保留 Giles 裁剪边界 |
| 接触面内部支撑采样 | 项目盒体采样；SCA 2017 §3.6 面内支撑 | researchContactPatch | 有重力的两个动态刚体在接触边界内增加一个均值见证点；固定面沿用边界点；进入原接触行和支撑图 |
| 压力条件 | 项目既有压力／长度数值条件 | researchTransferContactPressure | 二维沿用碰撞 margin；三维采用 0.5 mm；自由接触按原始 λ 在对偶后维持共享 ρ；关节接触在初始化与支撑转移时按 λ 加 LRC 反力维持条件 |
| LRC 及运动后支撑释放 | Müller 等人 SCA 2017 §3.6；项目刚体接入 | LongRange.cpp、ContactSupports.cpp、LongRangeConstraints.cs | 硬辅助界、支撑多边形与辅助反力归 LRC；全部三维刚性路径按相对接触运动、摩擦承载和下层凸包更新传播支撑；释放反力归原接触乘子；水平地面法向界进入同一次刚体 KKT |
| 支撑数据与装配开销 | 项目单份数据和等价装配 | researchFinishSupportRanges、researchBodyEquation | 支撑图直接生成 LongRangeData 的界、节点和权重；刚体块装配 LDL 读取的下三角；接触拓扑及既有水平位移／转角条件使缓存失效 |
| 接触状态与几何复用 | Bullet 持久局部见证点思路；项目 CPU 数据组织与运动门限 | ContactManifold、reuseContactGeometry、readContactOffsets | 流形持有端点索引、支撑标记、辅助反力、Newton 匹配数据及上次裁剪位姿；自由接触累计平移加转动表面位移在 10 μm 内时复用点集，每步重算误差、匹配中点、乘子衰减及压力条件；越过门限执行完整裁剪；各岛持有支撑求解缓冲 |
| 支撑图跨步复用 | 项目接触版本与几何生命周期 | SupportGraph、researchKeepSupportGraph、researchBeginSupportRanges | 世界持有接触拓扑与点集版本；支撑图记录构造时版本和位姿；自由接触世界的版本一致且全部刚体累计表面运动在 10 μm 内时复用界、节点、权重与接触引用，每步更新初始误差和辅助反力 |
| 并行调度与收敛 | 项目 AVBD 调度；Microsoft PPL | researchSolveRigidColors、researchUpdateRigidForces、researchAfterIteration | 关节颜色组按动态岛并行，纯接触分量由支撑状态选择传播次序；三维从第二轮检查 10 μm 更新与完整盒体间隙，使原始块观察首轮反力交接；1 mm 接触残差触发同一主循环继续求解；完整步计时包含检测、同步和全部实际迭代 |
| 三维 CPU 装配与 Release 编译 | Newton RIRᵀ 和接触 JᵀKJ；项目 CPU 代数装配，MSVC 全程序优化 | newtonAngularInertia、researchContactEquation、CMakeLists.txt | 等主惯量直接形成旋转不变的惯量矩阵；共享接触度量同时激活时直接形成 ρI 刚体块；保持调用方浮点环境，Release IPO 和内联，浮点表达式采用 /fp:precise |
| 有限材料 ALM | Newton 接触、关节和对偶内核 | NewtonRigid.cpp | s=K/(K+ρ)、k_eff=Kρ/(K+ρ)，投影 sλ+k_eff C；材料反力、ρ 和各自历史由对应行持有；同一块下降循环 |
| 零重力自由飞行 | AVBD 惯性块极小值；项目阶段选择 | demo_build_supports、researchBodySolves | 零重力省去高度支撑图；位姿达到惯性极小值的无约束刚体保持预测解；接触、关节和距离界使其继续参与原块下降 |
| 位置稳定化 | AVBD 论文 §3.6 | PositionStabilizationEnabled、Stabilization.cpp | 显式配置的额外位置块下降阶段 |
| 实际碰撞误差 | 盒体分离轴几何 | readBoxGap、独立双精度 OBB SAT | 从最终位姿求最小平移穿透深度；计时边界为完整 PhysicsDemo.Step，几何验收在计时外 |

Newton 默认 BOX/BOX 窄相采用 MPR/GJK 和 multicontact，本地采用 Giles 盒体裁剪与 Newton 点对适配。四点约简来自 Newton 的 multicontact.py，其 Jitter Physics 2 上游许可保存于 [Jitter-LICENSE.md](Jitter-LICENSE.md)。Newton 采用 [Apache 2.0](Newton-LICENSE.md)，Giles 许可随原参考源码保存。项目数值参数、内部见证点、CPU 调度适配及缓存由本地代码持有。

源码：[Newton 刚体内核](https://github.com/newton-physics/newton/blob/v1.7.0/newton/_src/solvers/vbd/rigid_vbd_kernels.py)、[Newton 调度与历史](https://github.com/newton-physics/newton/blob/v1.7.0/newton/_src/solvers/vbd/solver_vbd.py)、[分色](https://github.com/newton-physics/newton/blob/v1.7.0/newton/_src/sim/graph_coloring.py)、[接触匹配](https://github.com/newton-physics/newton/blob/v1.7.0/newton/_src/geometry/contact_match.py)、[多点流形](https://github.com/newton-physics/newton/blob/v1.7.0/newton/_src/geometry/multicontact.py)、[Giles 2D](https://github.com/savant117/avbd-demo2d)、[Giles 3D](https://github.com/savant117/avbd-demo3d)、[MSVC 全程序优化](https://learn.microsoft.com/en-us/cpp/build/reference/gl-whole-program-optimization?view=msvc-170)。

深度自适应 C0 来源为 [PhysX_AVBD 开源研究实现](https://github.com/Aniysk/PhysX_AVBD/blob/b46e2eed8d463dde833d9f348e7fdf2d39e7b3f9/avbd_standalone/avbd_solver.cpp)，仓库作者为 Aniysk。其 BSD 3-Clause 许可保存于 [PhysX-AVBD-LICENSE.md](PhysX-AVBD-LICENSE.md)。

二维局部见证点匹配参考 [Bullet 的持久流形缓存](https://github.com/bulletphysics/bullet3/blob/master/src/BulletCollision/NarrowPhaseCollision/btPersistentManifold.cpp)：本地采用两个端点中的较小局部距离，随后执行原有法向门限及一对一分配；距离门限仍为 0.5 mm。岛并行使用 [Microsoft PPL](https://learn.microsoft.com/en-us/cpp/parallel/concrt/parallel-algorithms?view=msvc-170)，各岛保留原有数值更新次序。

