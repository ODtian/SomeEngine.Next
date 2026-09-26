# Asset、Block 与使用对象

2026-09-20：本页描述本次重构后的实现。总体原则见 [重构原则](Asset-Rebuild-Principles.md)，导入协作见 [导入与工程存储](Asset-Import-and-Storage.md)，验证范围见 [本次验收](Asset-Verification-2026-09-16.md)。

## 身份和内容分开

Asset 是身份、引用和生命周期单位。`MeshAsset`、`TextureAsset`、`ShaderAsset`、`MaterialAsset` 和 `WorldAsset` 是独立资产类型，不绑定某一种使用对象，也不从使用对象逐字段生成资产镜像。同一身份可以有多个独立打开的对象及内容分支。

Block 是 Asset 内部开放、强类型的数据单位。一个 MeshAsset 可同时包含 `MeshBlock`、`MeshMaterialsBlock`、`ClusterMeshBlock`，以后也可加入其他管线的 Block。Scene 引用 `AssetId<MeshAsset>`，不引用某个管线的几何表示。添加一个 Block 不要求新增一个语义资产身份。

Asset 不分“文本资产”和“二进制资产”。Block 自己选择文本字段、二进制负载、外部来源、磁盘描述、延迟读取或仅在进程中存在的数据。一个逻辑资产可以组合多个不同存储方式的 Block；这不是“每个资产都有可读主文件，再引用二进制资产”的模型，也不要求每个资产都提供可读格式。现有文档容器可以同时包含文本编码元数据与二进制页，Block 的自定义读取也可以接入网络或生成内容。仅进程内 Block 不被伪装成可持久内容。

## 强类型查询与延迟取得

Asset 打开时根据目录建立自己的持久类型 ID 索引，后续取得复用该目录，静态泛型入口选择读取代码；查询不遍历全部 Block，不通过字符串、行为枚举或类型名称解释业务内容。

```csharp
using MeshAsset mesh = await MeshAsset.OpenAsync(path);
if (mesh.ContainsBlock<ClusterMeshBlock>())
{
    ClusterMeshBlock clusters = await mesh.GetBlockAsync<ClusterMeshBlock>();
    // 由 Cluster 集成读取自己需要的数据并建立使用对象。
}
```

`ContainsBlock<T>()` 只查询类型是否存在。`GetBlock<T>()` 只返回已取得的 Block；不会把磁盘读取藏在同步 getter 内。`GetBlockAsync<T>()` 显式取得所需 Block；同一个打开对象内的并发取得合并，等待者取消互不取消。已发布的 Block 由 Asset 持有，调用方不能把借用结果当作独立实例释放；需要独立使用期限时建立其分支或保留所需数据。

打开资产可取得少量元数据，但不保证所有 Block 或其全部负载驻留。已经取得一个 TextureBlock 也不等于已经解压所有 tile。未知 Block 的公共描述仍能被读取，未取得的内容可以原编码保存；真正请求它时才需要对应扩展的静态编解码能力。

## 页、分支和释放

Block 可以使用现有 `PageArena`、`PageBuffer`、`PageList<T>`、`PageBufferMap`、范围来源与块调度器，不另建页池、预算或页生命周期。Asset 协调同一内容分支的访问边界，具体字段和字节存储属于 Block。

`SetBlock` 将输入内容建立为该资产自己的分支，不偷走调用方的 Block。`Duplicate()` 保留资产 ID，隔离后续编辑；`CreateCopy()` 创建新的资产身份。普通 C# 引用赋值仍共享同一个对象。页级 COW 保证其他分支和保留读取者看到原内容，不深复制任意第三方可变对象。

Asset 关闭会等待自己发起的实际取得结束，再释放自己的 Block 和来源。另一个资产分支、已保留范围或已经创建的使用对象继续遵守各自的使用期限。释放不是保存。

固定字段布局由 SG 在声明端生成；变长数据的文件位置由现有文档与页目录在打开时解析并复用。不能把项目本次生成的 enum 下标当成跨文件持久位置。

## 从 Block 到使用对象

具体系统直接请求认识的 Block，以强类型代码完成必要读取、解码、依赖解析和资源创建。完成后的对象保证当前承诺可用的数据能够直接使用，不再通过 Asset 的异步接口访问这些数据。

Asset 与使用对象共用底层序列化和页管理机制。当前约定是 Asset 可部分驻留，小字段在异步创建完成前就绪，大数据通过显式异步操作取得；使用对象在其约定的使用周期内保留全部必需内存，普通读取无需异步。这个区别是使用策略，不是底层页系统只能支持两种状态的限制；未来可按用途提供其他部分驻留模型。

一个 CurveBlock 可以建立 Curve 或 CurveSamples；ConstantCurveBlock 也可建立 Curve。这种多对多关系不需要全局资产到对象转换表。业务对象保留普通属性、状态和设备资源，不反向引用资产设施。未来细节级别或新增后端内容仍通过明确的页流送或资产侧转换取得，再切换使用状态，而不是同步属性隐式读文件。

源码：[Asset](../../../src/SomeEngine.Assets/Asset.cs)、[Block 索引与生命周期](../../../src/SomeEngine.Assets/Asset.Blocks.cs)、[Block 基础能力](../../../src/SomeEngine.Assets/AssetBlock.cs)、[外部 Curve 样例](../../../samples/SomeEngine.Curves.Assets/CurveAsset.cs)。
