# Asset、Block 与使用对象

2026-09-29：本页规定 Asset、Block 与使用对象的公共模型。总体原则见 [重构原则](Asset-Rebuild-Principles.md)，导入协作见 [导入与工程存储](Asset-Import-and-Storage.md)。已执行验证按日期保存；当前实现未满足这些规则的位置见 [本轮审查](../../../reviews/AssetSpecReview-2026-09-29.md)。

## 身份和内容分开

Asset 是身份、引用和生命周期单位。`MeshAsset`、`SkeletonAsset`、`AnimationClipAsset`、`TextureAsset`、`ShaderAsset`、`MaterialAsset` 和 `WorldAsset` 是独立资产类型，不绑定某一种使用对象，也不从使用对象逐字段生成资产镜像。同一身份可以有多个独立打开的对象及内容分支。

`SkeletonAsset` 与 `AnimationClipAsset` 只声明身份和 Block 目录，通过资产生成器取得类型化引用、复制、保存和打开 API。骨架与片段的内容 Block 由动画程序集提供，导入扩展按来源 `.meta` 选择。

Block 是 Asset 内部开放、强类型的数据单位。一个 MeshAsset 可同时包含 `MeshBlock`、`MeshMaterialsBlock`、`ClusterMeshBlock`，以后也可加入其他管线的 Block。Scene 引用 `AssetId<MeshAsset>`，不引用某个管线的几何表示。添加一个 Block 不要求新增一个语义资产身份。

Asset 不分“文本资产”和“二进制资产”。Block 自己选择文本字段、二进制负载、外部来源、磁盘描述、延迟读取或仅在进程中存在的数据。一个逻辑资产可以组合多个不同存储方式的 Block；这不是“每个资产都有可读主文件，再引用二进制资产”的模型，也不要求每个资产都提供可读格式。现有文档容器可以同时包含文本编码元数据与二进制数据块，Block 的自定义读取也可以接入网络或生成内容。仅进程内 Block 不被伪装成可持久内容。

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

`TryGetBlockAsync<T>(CancellationToken cancellationToken = default)` 返回 `ValueTask<T?>`，类型缺失时返回 `null`；类型存在时使用相同的异步取得路径，读取错误与取消继续传播。`GetBlockAsync<T>()` 在类型缺失时抛出 `KeyNotFoundException`。导入期间，依赖方使用 [Context 的同名方法](Asset-Import-and-Storage.md) 先完成对应生产者。

`IAssetBlock<T>.Persistent` 默认返回 `true`。只在导入进程内使用的 Block 声明 `public static bool Persistent => false;`，Asset 持有并复制其内容，保存时只写持久 Block 的元数据、字节与引用；持久 Block 沿用已有文件编码与目录格式。

打开资产可取得少量元数据，但不保证所有 Block 或其全部负载驻留。已经取得一个 TextureBlock 也不等于已经解压所有 tile。未知 Block 的公共描述仍能被读取，未取得的内容可以原编码保存；真正请求它时才需要对应扩展的静态编解码能力。

## 数据、分支和释放

Block 的小字段使用普通 C# 值和集合，字节使用独立不可变数组。统一字节缓存记录已保留数组和实际读取任务；多个创建请求从同一份选定不可变来源复用结果，详见 [数据共享与修改](Asset-Sharing-and-Writes.md)。文件读取直接物化最终数组，不在文件层保留一套解码缓存。

`SetBlock` 建立该资产自己的内容分支。`Duplicate()` 保留资产 ID，复制需要独立修改的字段和集合结构，共享不可变元素与未变的数据节点；`CreateCopy()` 创建新身份。不承诺集合复制是常数成本，也不深复制任意第三方可变对象。

Asset 和 Block 关闭它们明确取得的来源；实际读取保持来源有效直到完成。已经返回的托管字节视图和纯数据使用对象依赖普通 GC 引用，不需要释放，也不会被回池覆写。固定字段编码由声明端生成，变长数据位置由文档目录提供。

## 从 Block 到使用对象

具体系统直接请求认识的 Block，以强类型代码完成必要读取、解码、依赖解析和资源创建。完成后的对象保证当前承诺可用的数据能够直接使用，不再通过 Asset 的异步接口访问这些数据。

Asset 和使用对象可以复用编码或读取能力，但不要求绑定同一个对象生命周期。Asset 可部分驻留，小字段在异步创建完成前就绪，大数据通过显式异步操作取得；使用对象以普通引用持有全部必需数据，普通读取无需回到 Asset。Shader 使用普通 GC 引用和直接 Slang 对象，Texture tile 与 Mesh 几何范围则可以保留自己的业务分页。

一个 CurveBlock 可以建立 Curve 或 CurveSamples；ConstantCurveBlock 也可建立 Curve。这种多对多关系不需要全局资产到对象转换表。业务对象保留普通属性、状态和必要设备资源，不反向引用资产设施。未来细节级别或新增后端内容通过明确的业务流送或资产侧转换取得，再一次发布完整状态，而不是同步属性隐式读文件。

源码：[Asset](../../../src/SomeEngine.Assets/Asset.cs)、[Block 索引与生命周期](../../../src/SomeEngine.Assets/Asset.Blocks.cs)、[Block 基础能力](../../../src/SomeEngine.Assets/AssetBlock.cs)、[外部 Curve 样例](../../../samples/SomeEngine.Curves.Assets/CurveAsset.cs)。
