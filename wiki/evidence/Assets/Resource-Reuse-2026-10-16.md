# 资源复用与引用空档证据（2026-10-16）

对应 [#216](https://github.com/ODtian/SomeEngine.Next/issues/216) 与 [#210](https://github.com/ODtian/SomeEngine.Next/issues/210)。通用索引的命中、原生句柄表、记录容量与分批检查见 [WeakValues 证据](../Core/Weak-Values-2026-10-16.md)。

## 输入与取得边界

`tools/SomeEngine.Assets.Benchmarks/ResourceReuseMeasurements.cs` 通过 `ShaderAsset.GetAsync` 和 `TextureAsset.GetAsync` 取得实际对象。输入为 1024×1024 R8 单 mip Texture、一份单计算入口的 Slang IR Shader，以及保存共同 target/入口代码的同类 Shader。Shader 采用 SPIR-V、`glsl_460`，固定同一 Slang 全局会话和设置。

输入文件与目录描述在计时前建立。测量用 `IAssetStorage` 保持选定版本的目录项，并打开实际 `FileRangeSource`。`IRangeSource` 包装记录成功读取的范围；IR、保存代码和像素的范围由公开 `AssetFile.DescribeChunkAsync` 与 Block 描述取得。每项计费分别记录打开次数、对应数据读取次数及字节数，文件头、目录和 Block 元数据等读取归入来源总字节。

读取器保留已打开文件时，`AssetFile` 及其 `ByteSource` 保持同一身份；同一份 Block 数据可命中字节 LRU。另一个读取器将 `MaxCachedFiles` 设为 0，随后取得重新打开文件并建立自己的 `ByteSource`，用以观察完整重新读取的成本。两组字节预算均为 64 MiB。

## 比较条件

任务票给出的比较基线为跨场景 Shader 复用与场景内 Texture 复用。基线 `f6c0f95d` 的 `AssetLoader.Resources.cs` 把完成任务保存在 Shader/Texture 表中，结果由表中的任务维持引用。本轮沿 `AssetResourceKey<TAsset>` 的来源读取器、资产 ID 与选定版本取得弱索引中的值，业务对象引用维持实际使用期。

“存活重复取得”和“保持引用跨场景”采用基线相同的实际对象仍存活条件；“新版本重叠”在原对象仍被持有时发布同一资产的新版本；“引用空档”使阶段内所有普通引用结束，通过外部弱引用和 GC 证明旧对象已回收，再次走完整取得入口。实例数通过引用相等和旧对象的可回收性观察，计数对应该受控取得序列。

## 费用归属

字节缓存记录预算、数组实际分配和索引容量。实际 Texture 持有的像素引用与字节缓存可以指向同一数组；新旧像素内容各自持有一份数组。弱索引保存取得键、短弱句柄及可复用的高水位数组，GC 回收实际对象后由后续取得推进键与句柄清理。

IR 路径分别记录 Slang 原生代码 blob 的可见字节；原生 blob 依据数据地址去重。保存代码路径分别记录实际 Shader 持有的托管 target/入口数组字节。这些字段表达当前公开代码数据。Slang 会话、IR、反射及编译器自身的原生分配计入进程私有内存总差值；该差值同时包括托管堆的页提交。取得期间的全进程托管分配由 `GC.GetTotalAllocatedBytes` 单列。GPU 上传和设备分配由对应渲染设备测量记录。

## CPU 取得结果

公共队列任务 `2a804e3d416a473ab2511269db060570` 于 2026-10-16 20:33:02–20:33:05（Asia/Shanghai）完成，退出码 0。Intel Xeon E5-2698B v3、Windows x64、.NET 10.0.13、Release、`DOTNET_TieredCompilation=0`。复用队列任务 `bbf7bbe4c0e641858fccada901edfce9` 的成功构建产物（0 警告、0 错误）：

```powershell
$env:DOTNET_TieredCompilation = '0'
& F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1 dotnet run --project tools/SomeEngine.Assets.Benchmarks/SomeEngine.Assets.Benchmarks.csproj -c Release --no-build -- reuse
```

本页的毫秒数为一次完整序列中的事件反馈，保留首次路径 JIT、读取及 Slang 创建费用。存活命中的预热耗时统计由通用索引的 9 轮证据量化。本组通过固定输入和相同设备/编译设置核对实际创建、读取和保留量：

| 事件 | 完整取得耗时 | 托管分配 | 打开 | IR 读取 | 保存代码读取 | 像素读取 | 来源总字节 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Texture 首次取得 | 222.609 ms | 1,119,320 B | 1 | 0 | 0 | 1 × 1,048,576 B | 1,050,008 B |
| Texture 新版本 | 14.945 ms | 1,090,560 B | 1 | 0 | 0 | 1 × 1,048,576 B | 1,050,008 B |
| Texture 引用空档后重建 | 2.181 ms | 22,576 B | 0 | 0 | 0 | 0 | 480 B |
| IR Shader 首次取得 | 62.727 ms | 91,616 B | 1 | 1 × 25,436 B | 0 | 0 | 27,673 B |
| IR Shader 新版本 | 10.868 ms | 89,664 B | 1 | 1 × 25,436 B | 0 | 0 | 27,673 B |
| IR Shader 引用空档后重建 | 8.213 ms | 45,608 B | 0 | 0 | 0 | 0 | 1,203 B |
| 保存代码 Shader 首次取得 | 49.390 ms | 263,160 B | 1 | 1 × 25,436 B | 2 次，共 558 B | 0 | 30,211 B |
| 保存代码 Shader 引用空档后重建 | 6.933 ms | 53,080 B | 0 | 0 | 0 | 0 | 1,431 B |
| 保存代码 Shader 文件重新打开后重建 | 7.974 ms | 124,104 B | 1 | 1 × 25,436 B | 2 次，共 558 B | 0 | 30,211 B |

Texture 和 IR Shader 的 32 次存活取得各保持同一引用，托管分配为 0 B，打开及 IR/代码/像素读取均为 0，来源总字节为 0 B。保持普通引用跨阶段取得，以及保存代码 Shader 的存活再次取得，同样为 0 B 托管分配、0 打开和 0 B 来源读取。这对应比较基线已经复用实际对象的情形。

Texture 和 IR Shader 各完成首次创建、新版本创建及引用空档后重建，共 3 个实际对象；保存代码 Shader 在主读取器中共创建 2 个对象。新版本阶段同时保留两个 Texture，共 2,097,152 B 像素；两个 IR Shader 的去重原生代码 blob 共 2,448 B。引用空档前分别观察到这两组旧对象各 2 个全部回收，保存代码组旧对象 1 个回收。重建后的单个 IR Shader 原生代码为 1,224 B，保存代码 Shader 的托管 target/入口代码合计 1,224 B。

IR、代码和像素的字节命中继续承担实际对象重建工作。文件仍在读取器中时，重建的来源读取量分别为 480、1,203 和 1,431 B，内容为元数据等读取。`MaxCachedFiles=0` 的两次取得分别打开一次来源，每次读取 IR 25,436 B、保存代码 558 B；新 `ByteSource` 形成对应的独立字节缓存项。

主字节缓存最终保留 7 项、数组 2,174,872 B、索引容量 1,184 B；重新打开文件组保留 6 项、数组 53,488 B、索引容量 1,184 B。主组包括两个版本像素、三个 IR 数据数组及两份保存代码数组，后者的磁盘 558 B 解码为 1,224 B 代码。重新打开组保存两次来源身份各自的 IR 和代码数组。索引数组保持高水位，实际对象和字节缓存分别按普通引用和预算表达保留。

进程私有内存变化单列为整体反馈：IR 首次创建增加 6,328,320 B，新版本增加 6,246,400 B，引用空档后重建增加 2,760,704 B；保存代码首次创建增加 6,049,792 B，空档后重建增加 2,166,784 B。重新打开文件的第二次取得变化为 −851,968 B，反映该事件同时发生的回收。代码 blob/数组大小与这些全进程差值分别记录，用于区分可见数据和编译器、会话及堆页费用。

## 行为检查

`ResourceReuseTests` 在公共队列任务 `b9a701ccd47b496a9c68d0e2f71d138e` 中通过 12 项，测试规则 0 违规，退出码 0。执行时间为 2026-10-16 20:27:42–20:28:01（Asia/Shanghai），测试执行约 1 秒：

```powershell
& F:/SomeEngine.Next/tools/Invoke-Benchmark.ps1 dotnet test tests/SomeEngine.Assets.Tests/SomeEngine.Assets.Tests.csproj -c Release --no-build --filter FullyQualifiedName~ResourceReuseTests -v minimal
```

Shader、Texture 各覆盖六条公开行为：同一存活值重复取得；32 个并发读取共享完整创建；不同读取器各自取得对象；同一资产的新版本取得新对象且旧值继续可用；GC 后沿同一来源创建新值；文件读取器结束后继续使用已取得的代码和像素。

检查通过公开 `IAssetStorage` 实现记录来源打开次数，通过 `ReadStarted` 和 `ReleaseReads` 明确交接读取阶段。GC 检查使用普通引用、外部弱引用及独立方法范围；引用结束后再次取得并实际读取代码或像素。Shader 设置、创建会话与索引范围在各项检查中保持一致。
