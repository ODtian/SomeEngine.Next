# Texture 资产与 tile 分块

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

`Texture.Asset` 表达可编辑纹理内容，应用中的 `Texture` 负责实际纹理语义、流式读取状态和设备关联。通用身份与数据规则见 [[Asset-Objects]]，文件流程见 [[Asset-File-IO]]。

## 根信息与负载

根信息描述尺寸、维度、层数、mip 数量、存储格式、采样格式与采样视图维度。tile 元数据描述某个 mip、层、face、深度切片及二维 tile 位置，并带有尺寸、行跨度、切片跨度与块引用。

**每个 tile 对应独立文件 chunk。** 磁盘资产打开后可以只保留 tile 元数据和块引用，负载仍留在来源中。内存构建的资产也可以明确提供 tile 负载。

tile 与文件块的关系是纹理类型语义，不能推广为所有资源都逐页独立分块。Mesh 的不同布局见 [[Asset-Meshes]]。

## 显式创建与流式读取

`Texture.CreateAsync` 或具体资产的 `LoadAsync` 建立应用对象，并按纹理加载规则取得所需初始内容。后续 `AcquireMipTileAsync` 明确请求特定 tile；属性读取不替调用方加载它。

来源保留允许纹理在 `.Asset` 和 Loader 关闭后继续流式读取。块请求、解码驻留和 GPU 分配具有各自的预算与生命周期，不能算进 Loader 的根编码缓存预算，见 [[Asset-Loading-and-Lifetime]]。

## 编辑与保存

只改名称等不影响块含义的根信息时，可以保存新的根编码，并从原来源流式写出未修改 tile。保存不能先把所有 tile 负载读到一个大数组中。

tile 尺寸、坐标、行跨度和负载必须按纹理语义保持一致。重复位置、越界或缺少必需负载等输入问题在相应保存或加载边界处理，不通过普通 getter 执行业务扫描，见 [[Asset-Validation]]。

tile 集合及单个 tile 的可变嵌套字段采用什么写入形式仍未确认。当前不可变记录 / 集合改动不能当作最终公共 API；共享原则和未决事项分别见 [[Asset-Sharing-and-Writes]]、[[Asset-Open-Questions]]。

## 设备关联与更新

Texture 持有自己的设备关联。RenderDevice 根据已取得的资源内容准备上传和视图，不能在渲染准备过程中隐式进行资产文件读取。

显式更新纹理内容时，已有对象身份保持；旧分配直到实际使用它的帧与队列提交结束后才可释放。取消或上传失败的处理继续沿用既有纹理更新机制，不建立资产专用 GPU 生命周期。详见 [[Asset-Application-and-Rendering]] 和 [[Render-Boundaries]]。

代码入口：[纹理字段声明](../../../src/SomeEngine.Assets/Schema/Texture.Fields.cs)、[tile 编码与块处理](../../../src/SomeEngine.Assets/Schema/Texture.Chunks.cs)、[加载与流式读取](../../../src/SomeEngine.Assets/Schema/Texture.Loading.cs)、[程序创建](../../../src/SomeEngine.Assets/Schema/Texture.Create.cs)。

返回 [[Asset-Files]]；关联 [[Asset-Materials]]、[[Asset-Verification-2026-09-10]]。
