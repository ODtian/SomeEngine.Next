# Grill 0004

## Q1: 本次交付是否必须是端到端完成，而不是只补公共接口声明？我的推荐答案是：是。本次目标是交付一个可实际投入 renderer 的 D3D12 3A RHI：公共契约、capability/limits、Null correctness backend、D3D12 native 实现、RG 映射、自动 harness、native output 验证、benchmark 与 soak 全部闭合；不允许接口存在但 backend 留 stub 或 `NotSupportedException`。在这套门禁全部通过前，不接入 cluster renderer。你接受这个终点，还是要修改其中的边界？
A: 对

## Q2: 本次“3A RHI”的功能边界是否采用下面这个终态？本次完整交付高端 GPU-driven raster/compute renderer 所需的 production core，包括 indirect draw、indexed draw、dispatch、count-buffer 和多命令执行；bindless/descriptor indexing 与稳定的 descriptor 生命周期；graphics/compute/copy queue、跨队列同步和多帧 retirement；committed/placed/alias resource、upload/readback、memory budget/residency telemetry；timestamp、occlusion、pipeline statistics 等 query；完整 graphics/compute pipeline、异步 PSO readiness 和持久 pipeline cache；swapchain、acquire、present、resize、vsync/tearing、HDR/color space；capability/format/limit 查询；device-loss reason、DRED、debug layer、GPU validation 与诊断；Null、D3D12、RG、harness、benchmark、hardware/soak 验证全部闭合。DXR、mesh shader、VRS、tiled/sparse resources、sampler feedback、work graphs 不在本次范围内，也不预留空壳 API；它们分别需要独立的端到端设计、backend 和硬件验收。本次交付的是完整的 3A raster/compute RHI core，不声称覆盖所有 D3D12 专项扩展。你接受这个范围，还是要求把哪些专项扩展也纳入本次端到端交付？
A: 确认，但是得记录DXR、mesh shader、VRS、tiled/sparse resources、sampler feedback、work graphs这些东西是否真的没有

## Q3: 本次公共 RHI 是否必须保持 backend-neutral，而只把 D3D12 作为本次完成并验收的 production backend？我的推荐答案是：是。`SomeEngine.Graphics` 和 RG 不得暴露 Vortice、DXGI、D3D12 command signature 等 native 类型；公共语义必须能够由未来 Vulkan/Metal backend 合理承载。此次只要求 D3D12 端到端实现，Null 作为 correctness oracle；Vulkan、Metal 不实现，也不因现有 `SpirV` 枚举而被视为本次交付。无法跨 backend 统一的能力通过 capability-gated extension 表达，不能污染 mandatory core。你确认这个 backend 边界吗？
A: 先对吧，但是缺少的api要记录下来

## Q4: 是否允许对当前 Graphics/RG 公共 API 做 breaking redesign，并一次性迁移仓库内所有消费者？我的推荐答案是：允许。当前 RHI 尚未 production-ready，保留不完整 API 的兼容性会把错误模型永久带入新 surface。可以修改或删除现有公共类型和方法，并原子迁移 Null、D3D12、RenderGraph、Render shader projection、tests、samples 和 docs；不保留 `[Obsolete]` 兼容层、重复旧入口或临时 adapter。新的 API 只有在完整 harness 通过后才视为冻结。仓库外部未知消费者不作为兼容约束。你确认这个 breaking-change 边界吗？
A: 允许

## Q5: 既然当前 cooked shader 和 WARP 测试基础已经是 SM 6.2，我建议本轮 D3D12 生产基线定为 Feature Level 12_0 + Shader Model 6.2，传统绑定必备，bindless 与 SM 6.6 direct heap indexing 都作为可选 capability；是否接受这个最低基线？
A: 对啊

## Q6: 我建议本轮必须交付的间接执行合同包括：间接非索引绘制、索引绘制和 compute dispatch；CPU 指定命令数量，以及由 GPU count buffer 指定实际命令数量；`argument-buffer offset + command stride + maximum command count`，支持一个 buffer 中的多条命令和 cluster 的 per-bin 布局；argument buffer 与 count buffer 可以是不同 buffer，也允许合法的同 buffer不同区域；RenderGraph 分别声明 argument/count 区域为 indirect read，正确生成 UAV-write → indirect-read 屏障和跨队列依赖；Null 后端验证 usage、状态、对齐、stride、范围、溢出、count 上限、队列类型和 rendering scope；D3D12 后端通过缓存的 native command signature 降低到 `ExecuteIndirect`；WARP/native 测试必须让 compute shader写入参数和 count，再真正执行 indirect draw、indexed draw、dispatch，并通过 render target/readback 或 UAV readback 验证实际结果，而不是只断言调用没有抛异常；不支持 bindless 时，这套间接执行仍然完整可用；D3D12 那种由每条间接记录额外修改 VBV、IBV、root constants、CBV/SRV/UAV 的“混合 command signature”暂不进入通用 RHI，把它作为已确认但本轮未实现的高级 API 缺口记录。是否接受这个间接执行边界？
A: 对啊

## Q7: 正确目标仓库是 `F:\SomeEngine.Next`，而 `F:\SomeEngine` 只应作为真实 renderer 和误落 RHI 实现的只读来源，对吗？
A: 反了

someengine是旧仓库啊，next是新仓库，我手动打包了一份第三方重新实现的rhi和rg到next，并在next继续优化，我想问是不是给我把优化写到旧仓库里了。看019f3bf1-8feb-7300-94e2-8da843980a2d

## Q8: `ShaderParameters` 的 SG 与现有 asset 系统怎么分工？要不要锁成：`ShaderParameters` 只描述参数形状，不指定 shader、entry、asset path 或 asset handle；SG 只根据 C# 参数类型生成静态字段 schema、字段读取代码、constant packing、view/descriptor 写入代码和无反射、无字符串查找的调用 glue；现有 asset 系统继续由 `SlangShaderImporter -> ShaderAsset -> entry point reflection -> runtime Shader -> MaterialPass / pipeline` 提供唯一 shader 真相；MaterialPass / pipeline 创建时把生成的参数 schema 与 ShaderAsset reflection 配对并缓存 immutable binding contract；RG compile 根据 contract 推导 access/view/barrier/lifetime；Execute 使用同一 contract 和 SG 代码写 descriptor 并绑定，热路径不反射、不按字符串匹配。锁吗？
A: 对啊

## Q9: 每个 shader-backed pass 的 shader/pipeline binding contract 是否必须在 Graph Build / AddPass 时确定，使 `PassParameters + MaterialPass/pipeline + asset reflection` 在 RG 编译前生成 access、barrier、lifetime 和 aliasing；Execute 只能消费已编译 contract，不能首次查 reflection、补登记访问或换成 access 结构不同的 shader？
A: 对啊

## Q10: 是否复用 RG 已有的 `TextureId / BufferId` 作为生成式参数模型里的逻辑资源引用，由 SG 替代当前用户手写的 `TextureAccess / BufferAccess`，而不新增另一套 RG 资源句柄？
A: 行行行，下一个问题

## Q11: Shader resource 参数是否使用单个 readonly unmanaged value struct，原子地携带已有 `TextureId / BufferId` 与完整 view 描述，禁止靠两个平行属性的命名约定拼接？
A: 好好好，下一个问题

## Q12: SomeEngine 现在先保留显式 `Read/Write + TextureAccess/BufferAccess`，只砍 RG descriptor reservation；PassParameters/ShaderParameters 作为后续最终模型，不在这轮硬塞，行不行？
A: 那就统一改成用 pass parameters / shader parameters
