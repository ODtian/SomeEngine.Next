# Residency Usage

Usage feedback 表示已经 resident 的 page 是否近期被使用，用于 eviction policy；它与缺页请求无关。

## Invariants
- fault 负责请求缺页，usage 负责 residency/eviction policy。
- usage 可以延迟读取。
- quality、time、memory budget 由 Cluster 上层提供；streaming 不因 queue overflow 临时改变 LOD 算法。

## Visible demand

Traversal 为每个 active leaf 的页范围更新最大 view/category priority；已经 resident 的页也进入独立的 compact demand 队列。fault 队列仍只表达不可用的 hierarchy part，其 reported/dropped 诊断不混入 resident demand。

两条 GPU 队列分别有固定的 PageFaultCapacity 个记录。每个几何页通过 priority 的零值去重，只追加一次；因此 reported count 也受已注册几何页数量约束。回读只包含固定队列区段，不复制稀疏的全页 priority 数组。CPU 按 RenderGraph.FrameIdentity 保留最近已完成帧的有界 demand，而不是按 callback 到达顺序。接收者在创建时固定驻留 epoch；旧帧的 fault 仍参与独立诊断与读取，但不覆盖当前 demand 的保护集合或 quality。截断观测不用于调整 quality，不建立 overflow replay 或额外 LOD fallback。

消费 demand 时先补齐同组与祖先页，再刷新 resident 页的 LRU。当前已收到 demand 的全部依赖在更新的已完成帧 demand 到达前不得作为 eviction victim。截断帧只声明已收到的需求及依赖；它不延续不可见的旧请求或旧 eviction 保护，后续 traversal 会重新提交仍需要的页。可用空间不足且没有可回收页时沿用 Deferred，等待后续 demand/retirement；不会淘汰当前被引用的依赖。

`MeshPages` 按注册时的连续页号保存来源、流送字节数及前后驻留页号，整数链接维护即时 LRU 顺序。当前视图的引用保护由同一对象中的紧凑位表保存；每份 demand 在 mesh 锁内清零位表，并通过一次遍历登记引用、累计流送字节、刷新驻留顺序和收集加载候选。页的注册与释放同时维护对应页记录和引用位表。

quality 的压力来自完整 demand 中 resident 与尚未 resident 的可流送页总字节数，以及扣除常驻粗页后的 heap 容量。持续超预算/低预算的帧计数分别饱和在 2/30，不依赖缺页数量。

## Parent priority and shared pages

祖先页以 child priority + 1 请求；uint priority 饱和而不回绕。当前 cook 允许相邻组或不同 LOD 的 clusters 共处一页，因此不能直接沿 page/group 环反复加一。注册 mesh 时从不可变组页范围建立重叠区间 component，再压缩 parent 边的强连通分量，并缓存 child-before-parent 顺序。跨页混合 LOD 形成的物理 parent 环使用同一个 component；只保留真实页区间，不纳入区间之间的无关页。一个 component 的页使用相同最大 priority；只有跨 component 的 parent 边提升 priority。每次 demand 传播只访问每个可达 component 一次，不在每帧重建拓扑。

淘汰优先沿既有逐页 LRU 选择。冷 parent 环没有单页候选时，复用同一 component，一次暂存其全部可用页的卸载；常驻粗页、当前需求、正在请求的 component 和 component 外仍驻留的子页继续阻止卸载。整批暂存失败时取消已暂存的全部成员。页的 fixup、提交和 GPU 完成后的容量归还沿既有 publication 一起执行，不在环内任意断开依赖。

该适配对应固定参考 [UE NaniteStreamingManager.cpp](https://github.com/EpicGames/UnrealEngine/blob/396c9f059903aed5fec78ecd3d437a40c6415368/Engine/Source/Runtime/Engine/Private/Rendering/NaniteStreamingManager.cpp)：2407–2429 区分 registered/new requests，2579–2665 提升 parent priority，2751–2772 统计全部可见需求并刷新 registered LRU，2904–2924 排除当前引用页并在无候选时停止本次分配。

截断处理也遵循同一参考的 [NaniteReadbackManager.cpp](https://github.com/EpicGames/UnrealEngine/blob/396c9f059903aed5fec78ecd3d437a40c6415368/Engine/Source/Runtime/Engine/Private/Nanite/NaniteReadbackManager.cpp) 94–127：选择最近完成的回读并钳制请求数；StreamingManager 2740、2838–2843、3009 在每次更新重建请求集合并清除上一轮的引用标记。这里保留既有的“不用截断观测调整 quality”规则。

RenderGraph 按 slot 顺序收集回读，而 slot 可先被复用。因而提交帧 1/2/3 后复用首 slot 提交 4，可能得到 callback 顺序 4/2/3（RenderGraph.CollectCompleted、RenderGraphNextFrame.TryOpenNextFrame）。demand 使用图已有的帧身份比较，较旧 callback 不能让驻留策略回到旧视图；没有另建提交时钟。
