# Asset 缓存与规范对齐验证

2026-10-01，工作区 F:/SomeEngine.Next。当前缓存规则见 [统一 CPU 字节缓存](Asset-Byte-Cache.md)。本轮接入数组中的整数链接 LRU，并处理 [2026-09-29 审查](../../../reviews/AssetSpecReview-2026-09-29.md) 中剩余的 Asset 实现偏差。

## 实现与公开行为

AssetByteCache.Shared 默认提供 512 MiB 的 CPU 字节保留预算。Texture tile、Mesh page、BVH 和保存的 Shader 字节共用缓存；预算包括当前保留的数据数组和动态索引数组。记录保存在结构体数组，Previous、Next 是整数下标，Dictionary 定位记录，空记录复用。读取期间的任务放在缓存级任务表，成功或失败后移除。

消费者通过普通强引用持有最终数组。公共行为测试验证 LRU 淘汰、预算缩减为零、读取期间 Clear、独立取消、失败重试、同来源重新取得和外部引用继续有效。缓存键绑定选定不可变文件来源及其 chunk/范围；Loader 保留的同一来源复用字节，独立来源完成各自的校验。来源身份只作为值进入缓存，实际 I/O 保留文件直到完成。

目录重建使用 .meta 的 ElementIds 恢复归属，发现使用已注册来源格式，来源保存直接维护主要身份。目录保留自引用事实。Asset 的所有关闭调用等待共同完成任务。材质 UpdateAsync 接收与创建一致的显式加载参数。

ShaderSourceBlock 统一源码和序列化 Slang 数据的编译入口；CompileAsync 返回直接 Shader、序列化数据及 Slang 文件系统实际提供的字节摘要。Importer 复用该入口。原生 ../ 来源与保存后的加载采用一致路径规则。应用选择可用的 Shader Block，按资产保留当前成功 Shader 版本，并通过读取序号处理完成顺序；失败保留此前结果。

glTF 默认要求持久元素 ID，采用下标身份的默认球体和导入场景在 .meta 中明确记录 RequirePersistentElementIds=false。默认转换选择通过项目注册能力取得，显式禁用保持有效。Partial 设置通过类型信息合并调用方默认值。持久 Asset/Block 名称在生成声明中明确填写，Block 编码名称默认沿用持久名称，CLR 重命名保留编码身份。

## 构建、回归与 AOT

| 检查 | 结果 |
|---|---|
| SomeEngine.slnx Debug 构建 | 通过，0 警告、0 错误 |
| Game.App Release 构建 | 通过，0 警告、0 错误 |
| SomeEngine.Assets.Tests 全量 | 240 项通过 |
| SomeEngine.Assets.Consumer.Tests 全量 | 88 项通过 |
| SomeEngine.Serialization.Tests 全量 | 203 项通过 |
| SomeEngine.Resources.Tests 全量 | 14 项通过 |
| Render AssetLoaderTests / RenderWorldExtractorTests | 37 项通过 |
| Assets Native AOT win-x64 发布与实际运行 | 通过，assets-import-nativeaot:ok、assets-nativeaot-smoke:ok |

上述回归共 582 项，具名组合是全量集合的子集。Slang 保持官方 NuGet 2026.17.1；包来源映射已加入官方包。V01 的原组合加 SlangComLifetimeTests 完成 64 项通过，完整 Asset 进程完成 240 项通过。该组合包含强制 GC、不可达 Shader 回收、失败后继续编译和已创建对象的有效性。

本轮将 Importer 的 ThreadStatic 全局会话改为进程级编译服务；Block 按会话串行访问编译入口。修订前 2026.17.1 的全量进程和单独评估的官方 2026.18.2 组合都出现 ComObject.Finalize → Marshal.Release 访问冲突。恢复固定 2026.17.1、修正编译服务持有关系后，上述原组合及全量回归通过。这些结果支持会话持有关系影响回收顺序的推断，证据范围是实际执行的托管回收行为。

## 当前缓存实现的公开 API 基准

命令：dotnet run --project tools/SomeEngine.Assets.Benchmarks/SomeEngine.Assets.Benchmarks.csproj -c Release --no-restore -- cache。

.NET 10.0.12，Release 优化，Benchmark 项目默认 JIT 配置。4096 个单位，每单位 256 B；预热后 9 轮，每轮 1,048,576 次固定种子的随机读取。同批结果分别通过消费者的 ReadOnlyMemory 数组和公开 AssetBlockSource.ReadBytesAsync 取得，校验和为 133,693,440。

| 项目 | 实测 |
|---|---:|
| 消费者直接访问中位 | 12.14 ns |
| 资产缓存取得中位 | 278.05 ns |
| 资产缓存取得轮次最大值 | 282.98 ns |
| 九轮缓存命中分配总量 | 0 B |
| 数据数组计费 | 1,146,880 B |
| 动态索引计费 | 521,112 B |

该公开路径包含来源键生成、已声明 chunk 查询、缓存查询、锁和 LRU 更新。与此前研究用原型的内部索引测量分别记录。缓存取得承担上述额外成本，业务对象正常访问直接使用其持有的数据。256 B 的小单位使索引比例明显；按业务 chunk 和范围计费的规则保持一致。

## 内容与统一场景平台

默认内容构建完成 67 项资产。Sponza 完成 96 项导入资产，Bistro 完成 2046 项导入资产及 1532 个 mesh instance；重复导入验证通过。

tools/Measure-Scenes.ps1 的本轮反馈耗时 43.32 秒，报告 Complete=true，三场景均为 Measured。1280×720，TieredCompilation=false，TAA、VirtualShadows、AsyncCompute 开启，几何预算 256 MiB，FixedFrameSeconds=1/60；球体动画开启，Sponza 和 Bistro 静态。TraceEnabled=false。设备 NVIDIA GeForce RTX 3080，驱动 32.0.16.1074，.NET 10.0.12。

| 场景 | 帧数 | Mean ms | Median ms | P95 ms | Prepare ms | Execute ms |
|---|---:|---:|---:|---:|---:|---:|
| 1024 spheres | 1459 | 1.3686 | 1.3460 | 1.5282 | 0.2173 | 0.7888 |
| Sponza | 1222 | 1.6351 | 1.6221 | 1.8361 | 0.0772 | 0.9742 |
| Amazon Bistro | 651 | 3.0771 | 3.0592 | 3.4400 | 0.0979 | 1.9779 |

以上是呈现帧耗时与 CPU 阶段计时。本轮内容按当前 schema 重新构建，场景结果作为当前版本的完整反馈；缓存本身的直接开销采用前面的公开 API 基准衡量。

临时基准数据、测试反馈文件和场景输出目录在结果摘录后删除，保留本页的参数和数值。正式基准工具、回归测试、构建产物及重新生成的项目内容按各自用途保留。
