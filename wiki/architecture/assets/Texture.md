# Texture

## Definition
`SomeEngine.Resources.Texture` 是业务使用对象，保存纹理描述、当前就绪像素和设备关联。持久资产身份及按需取得由 TextureAsset / TextureBlock 提供；一个 Texture 可关联多台设备。

## Contains
- dimension
- width / height / depth
- mip count
- array layer count
- format 与默认 sampled format/dimension
- revision
- streaming 所需的 chunk/mip 元数据

## Invariants
- Material 保存 Texture，不保存 Graphics.Texture 或 SRV/UAV。
- Texture 按 Graphics.Device 持有设备数据关联；具体设备数据持有 Graphics.Texture 和视图，并沿既有队列完成规则处理替换和释放。
- 关闭 Texture 撤掉自身的像素引用并请求释放各设备关联，已返回的托管像素视图继续有效；移除一台设备的关联不影响其他设备。
- 不为设备状态再建立第二个公开 Texture 身份。

同一不可变字节来源的创建请求通过统一预算缓存共享 tile 数组；消费者持有普通强引用。LoadMipAsync 必须匹配当前内容版本，不能因尺寸相同就把旧版本像素写入新纹理。

## Relations
[[Texture-Bootstrap]] · [[Texture-Device-Realization]] · [[architecture/rendering/RenderDevice]]
