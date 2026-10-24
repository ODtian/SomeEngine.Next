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
- Texture 的内部 TextureDevices 按 Graphics.Device 保存设备数据关联；具体设备记录持有 Graphics.Texture 和视图，关联查询清理已经失效的记录。
- 业务、Material、Rendering world 和持久图使用方通过普通引用持有实际 Texture。Texture 的像素与状态随对象存活；已返回的托管像素视图通过自己的引用存活。
- 最后一处实际使用结束后，TextureDevices 的终结路径向对应 RenderDevice 入队释放请求。渲染线程在 CollectCompleted 处理请求，沿已登记帧与实际 GPU 完成边界释放分配和视图。
- 移除一台设备的关联推进分配版本，其它设备继续使用自己的数据；完整更新继续由其余参与设备推进。
- 不为设备状态再建立第二个公开 Texture 身份。

同一不可变字节来源的创建请求通过统一预算缓存共享 tile 数组；消费者持有普通强引用。LoadMipAsync 必须匹配当前内容版本，不能因尺寸相同就把旧版本像素写入新纹理。

## Relations
[[Texture-Bootstrap]] · [[Texture-Device-Realization]] · [[architecture/rendering/RenderDevice]]
