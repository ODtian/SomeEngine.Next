# TextureAsset 与按需纹理数据

2026-09-16。TextureAsset 提供身份和 Block 目录，TextureBlock 保存尺寸、维度、层数、mip、格式、采样视图及 tile 描述。Texture 使用对象持有已经取得的像素和设备关联，不是潜在资产文件的包装。

## tile 与取得

glTF 的 `GltfTextureImporter` 先异步读取外部图像字节，或取得嵌入图像自己的字节数组，再通过 `ImportContext.Scheduler.ScheduleAsync` 提交独立图像解码。每张图像的来源数组、解码状态和 RGBA 输出由本次调用保持；Job 完成后，导入续体集中填写 `TextureBlock` 的尺寸、tile 与像素并安装 Block。失败经 Job 结果传播，取消与 Context 关闭保持读取及解码到真实完成后再清理。公开导入、保存重开和 `TextureBlock.CreateAsync` 的像素消费见 `GltfTextureImportTests`。

每个 tile 对应独立文件 chunk，记录 mip、层、face、深度切片、二维位置、尺寸、行跨度、切片跨度和内容键。打开资产可以只取得这些元数据，像素仍未解码。

`TextureBlock.CreateAsync` 默认取得最粗 mip，并保留本编辑分支已经提供的像素；调用方也可明确选择起始 mip。所选像素在返回前完成取得，Texture 直接持有托管字节，业务与渲染使用方通过普通引用保持它的使用期。

需要按资产共用实际对象时，业务把自己的 `WeakValues` 交给 `TextureAsset.GetAsync`。具体取得方法覆盖打开资产、读取 Block 和创建 Texture；同键存活对象与进行中的完整创建由弱索引复用。独立创建继续通过 TextureBlock.CreateAsync 表达，取得后的修改由实际 Texture 的数据和版本保存。AssetLoader 持有文件读取与字节缓存。

进一步细节通过 Block 的 `LoadMipAsync` 显式取得。加入像素前核对纹理描述及内容修订，防止将旧资产的像素接入已经更新的对象。Texture 自身不打开来源，渲染准备不隐藏文件读取。

## 编辑、保存与更新

tile 描述采用强类型值和集合，完整元素赋值与共同页写入产生隔离。调整位置、格式或跨度时必须保持负载和声明相符。保存在隔离分支中计算本次排列，已修改像素按页写出，未加载 tile 沿原文档复制，不先积累所有解压结果。

TextureBlock 的显式更新先建立替换内容，再交给同一个 Texture 的 `UpdateAsync`。业务启动更新后持续推进帧，各设备完成替换后提交当前字段与整份内容 Revision。旧资源由已有帧和队列完成机制保护。

设备关联由 TextureDevices 与现有设备层持有；每个设备具有独立分配和视图。`Texture.Versions` 同时表达当前内容、可用像素、设备替换内容与设备分配。LoadMipAsync 补充像素时推进 PixelsRevision，保持整份内容 Revision；RenderDevice 在现有分配补充上传。分配更换及设备关联退出推进 Allocation。Rendering world 通过已有提取保存这些版本，管线据此刷新图中的纹理引用。完整使用期与设备关闭规则见 [[Texture-Device-Realization]]。

验证覆盖多维纹理、分块、旧读者、取消、预算、多设备及实际输出；真实纹理测量覆盖 4 KiB、2 MiB、16 MiB，见 [本次验收](Asset-Verification-2026-09-16.md)。实现：[TextureAsset](../../../src/SomeEngine.Assets/Schema/TextureAsset.cs)、[TextureBlock](../../../src/SomeEngine.Assets/Schema/TextureBlock.cs)、[资源对象](../../../src/SomeEngine.Resources/)。
