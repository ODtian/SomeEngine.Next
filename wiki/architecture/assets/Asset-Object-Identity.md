# Asset 与使用对象身份

## Definition
已经加载的 Texture、Material、Mesh、Shader 等对象本身就是业务侧直接引用对象；资产身份、业务对象和热重载协调不通过第二个通用包装对象分裂。对象是否原地更新由该类型的语义决定，不是 Asset 系统的统一身份承诺。

## Invariants
- AssetId 只承担序列化、软引用、尚未加载引用和依赖描述。
- GameWorld、RenderWorld 与 renderer 直接保存已加载对象引用。
- loader 内部可以有自己的 single-flight 与加载协调数据，但不把加载状态暴露进业务 API。
- 可安全原地更新且确实需要稳定业务身份的对象，可以在自身明确更新边界保留引用。
- 整体生成、整体替换的不可变对象创建新实例，由已经保存当前对象的消费方一次交换外层引用。`Shader` 使用这一规则；它没有 `Update`、`Dispose` 或可交换的内层 Content。
- 构建失败或取消时不交换引用，旧实例继续可用；已经开始的异步消费者继续持有自己捕获的旧实例。
- Texture、Material、Mesh、Shader 是 Resources 中的业务使用类型；Assets 提供相应资产身份、Block 和转换。序列化 schema 不成为 renderer 所依赖的业务类型层级。
- 不建立 AssetTexture → RuntimeTexture → Graphics.Texture 一类重复长期对象链，也不为版本交换增加全框架通用的引用盒。

## Relations
[[Hot-Reload]] · [[Asset-Update-Boundary]] · [[Texture]]
