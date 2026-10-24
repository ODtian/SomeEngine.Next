# 托管资产数据与业务节点共享验证

2026-09-30，工作区 `F:/SomeEngine.Next`。本记录覆盖本次通用 CPU 页存储迁移及中断后的收尾，不把原 43 项任务或全部 Asset 审查问题算作本轮全部通过。设计见 [数据共享与修改](Asset-Sharing-and-Writes.md)，旧问题及后续状态见 [2026-09-29 审查](../../../reviews/AssetSpecReview-2026-09-29.md)。

> 下列构建、回归、AOT 和场景结果对应此前实现的节点弱引用模型。后续新增的“统一缓存预算保活、每条弱引用内存与访问成本不高于强引用”尚未满足，不得用这些通过结果宣布新要求完成。

## 实现范围

普通 Block 属性与集合使用 C# 数据，Duplicate 复制可编辑集合结构，共享不可变数据和未变的数据定义。通用 CPU 页容器、页级 COW、页适配流与每文件 ChunkRequestScheduler 已删除。编码继续使用静态序列化、标准缓冲接口和 BinaryDocument；未加载且未修改的 chunk 保存时复制原存储范围。

文件读取直接写入或解压到实际长度的最终数组。整 chunk 和 Mesh 范围共用内部 [AssetBytes](../../../src/SomeEngine.Assets/AssetBytes.cs) 的共同读取、弱引用与重试机制，状态属于对应内容定义的节点。使用方取得普通 ReadOnlyMemory，不取得需要配对释放的托管数据包装。内存中显式赋给 Block.Chunks 的数据由 Block 强引用。

弱引用的前提是长期存活的文件内容定义不保留无人使用的已载入数组；它不是普通引用复用的必要条件。该策略允许数据被 GC 回收后重新读取。如果改为随 Block 存活而保留数据，可以使用强引用，同样不需要手工计数或 Dispose。独立打开的 Asset 不因 ID 相同而自动合并内容定义。

Texture 按内容版本补充 tile，旧 Block 不得向更新后的同尺寸 Texture 加入旧像素。Texture 的设备关闭不使已返回的托管视图失效。Mesh 直接保留页和 BVH；创建入口完成最低细节页，补页会增加变更状态并使下一次 CaptureData 观察到新页。旧提取内容由实际读者保留，Mesh 不保存全部历史版本。

收尾时修复了内存 Mesh 的验证会改变数据版本、重复创建无法共享几何数组的问题，以及直接编辑页描述后继续使用旧数据定义的问题。对应公共行为测试先复现失败，再随修复通过。

## 当前代码的构建与行为结果

下列测试在共用读取机制收敛后实际执行。所有计数只对应列出的范围。

| 检查 | 结果 |
| --- | --- |
| SomeEngine.slnx，Debug，--no-restore | 0 警告、0 错误 |
| DefaultProject/Game.App，Release，--no-restore | 0 警告、0 错误 |
| AssetDataSharingTests、AssetBlockBehaviorTests、TextureMipTileStreamingTests、DirectSlangShaderTests | 29 通过、0 失败 |
| SomeEngine.Assets.Consumer.Tests 完整项目 | 85 通过、0 失败 |
| SomeEngine.Resources.Tests 完整项目 | 14 通过、0 失败 |
| SomeEngine.Serialization.Tests 完整项目 | 203 通过、0 失败 |
| RenderWorldExtractorTests、RenderDeviceTextureTests、AssetLoaderTests | 50 通过、0 失败 |
| Assets NativeAOT，Release/win-x64，重新发布并实际执行 | assets-import-nativeaot:ok；assets-nativeaot-smoke:ok |
| Serialization NativeAOT，执行本次迁移前半段已发布的产物 | serialization-nativeaot-smoke:ok |

上述测试共 381 项通过，不含下节中止组合已执行的部分项目。行为覆盖同一内容定义及未变 Block 分支共享数组、并发只读一次、等待者独立取消、失败重试、来源关闭后的视图有效性、无使用方后回收、不同内容版本隔离、最低几何取得和渲染接入。断言通过公开 API 和数据视图观察结果，没有读取私有字段或检查生成源码形状。

## V01 继续未关闭

当前代码再次执行初次审查中的同一组合：

```powershell
dotnet test tests/SomeEngine.Assets.Tests/SomeEngine.Assets.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~AssetFileOperationsTests|FullyQualifiedName~AssetImportTests|FullyQualifiedName~MaterialReloadTests|FullyQualifiedName~TextureMipTileStreamingTests|FullyQualifiedName~DirectSlangShaderTests|FullyQualifiedName~AssetBlockBehaviorTests' -v quiet
```

测试进程在 26 项已执行项目通过后中止，退出码为 1：

```text
Fatal error. 0xC0000005
System.Runtime.InteropServices.Marshal.Release(IntPtr)
System.Runtime.InteropServices.Marshalling.ComObject.Finalize()
System.GC.RunFinalizers()
```

中止组合不计为通过；较小子集成功不能覆盖这次失败。此次没有定位原生崩溃根因，也没有恢复手工 COM 释放、全局保活或页生命周期来绕过它。当前结果只能支持上述存储迁移的定向行为，不能宣布 Asset/Slang 综合验收全部完成。

## DefaultGame 三场景反馈

执行 `tools/Measure-Scenes.ps1`，未启用 trace。当前构建的反馈含启动共 **46.27 秒**，报告 Complete=true、Error=null，三个场景全部 Measured，没有缺失场景或未结束的加载。下面时间是呈现帧经过时间和 CPU 阶段时间，不是 GPU duration。

| 场景 | 帧数 | Mean ms | Median ms | P95 ms | Prepare ms | Execute ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1024 小球 | 1081 | 1.8493 | 1.7211 | 2.5106 | 0.2623 | 1.0140 |
| Sponza | 1153 | 1.7334 | 1.6040 | 1.9947 | 0.0829 | 1.0069 |
| Amazon Bistro | 646 | 3.1005 | 2.9239 | 3.7316 | 0.1152 | 2.1861 |

环境为 RTX 3080、驱动 32.0.16.1074、.NET 10.0.12；三个场景均为 1280×720。TieredCompilation=false，TAA、虚拟阴影与异步计算开启，GeometryMemoryMegabytes=256，固定场景步长 1/60 秒。小球场景动画开启，Sponza 和 Bistro 动画关闭。

场景结束时 QueuedLoads、ActiveLoads、BackpressuredPages 均为 0。小球页驻留为 6/6，Sponza 为 94/94，Bistro 为 1960/2379；Bistro 的非驻留页属于当前视角无需取得的内容，不代表仍有未完成加载。

这是当前构建的多场景反馈，没有同条件修改前基线；本次采样期间另有 AOT 发布进程，因此不据此宣称性能提升或退化。中断前另一次反馈含启动 47.61 秒，也为三个场景全部 Measured，但它发生在最后 Mesh 共享修复之前，不替代当前代码验证。

## 文档与临时内容

现行导航、共享规则、对象创建、Mesh、Texture、Shader、生成器、文件编码和任务说明已经迁移到托管数组与业务节点模型。带日期的旧审查和测量保存当时事实，并链接较晚的修复与失败，不作为恢复页系统的要求。

原始临时场景 JSON 在提取结果后清理；本次测试创建的临时来源和资产不作为交付保留。正式测试、工具及本页证据继续保留。

## 后续逐引用成本约束的本机核对

用户明确要求弱引用索引配合全局缓存预算控制强引用保活，并将成本口径确定为“每条引用的内存和访问成本均不得增加”。这与此前不设置额外字节保活缓存的模型不同，尚未实施；本轮没有将约束换成总内存或摊销成本。

在本机 .NET 10.0.12 进程内建立 100,000 个索引位置，全部指向同一个预先存在的 byte[]。强引用使用 byte[][]，弱引用使用 WeakReference<byte[]>[]。目标一直由独立强引用保活，两组执行相同的 3,000,000 次访问并取得相同求和结果。用 GC.GetAllocatedBytesForCurrentThread 记录构造分配，用 Stopwatch 记录预热后的访问。

| 索引 | 托管分配 | 单次读取 |
| --- | ---: | ---: |
| 普通强引用 | 800,024 B | 2.91 ns |
| WeakReference<byte[]> | 3,200,024 B | 8.17 ns |

该次测量中，弱引用每条额外分配 24 B 托管内存；尚未计入原生 GC 句柄存储。时间是一次进程内探针的观测，不是跨机器固定数字，也不是渲染性能结果。仅已观测的额外内存就不满足用户确定的严格口径。

CLR 的 [WeakReference<T> 实现](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/WeakReference.T.cs) 使用 GC 句柄表示目标，[官方弱引用说明](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/weak-references) 同样明确其内存取舍。缓存中的强引用可以让命中路径直接访问数据，却不能消除弱索引自己的对象与句柄成本；改用裸弱句柄只把对象成本转移为句柄表存储，不能据此宣称没有逐引用额外开销。

探针用进程内 Add-Type 执行，没有保存源文件、程序集或原始结果。当前需要确定相互冲突约束的取舍：允许每个索引目标承担弱引用成本并保持业务对象的普通强引用访问，或者严格保持强引用成本并由预算缓存主动撤掉索引中的强引用。后者在撤掉后不会自动找到仅由外部消费者保活的数据。
