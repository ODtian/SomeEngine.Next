# Asset Object Identity

## Definition
已经加载的 Texture、Material、Mesh 等对象本身就是业务侧长期引用对象；资产身份、运行时业务身份和热重载引用身份不再通过第二个包装对象分裂。

## Invariants
- AssetId 只承担序列化、软引用、尚未加载引用和依赖描述。
- GameWorld、RenderWorld 与 renderer 直接保存已加载对象引用。
- loader 内部可以有自己的 single-flight 与加载协调数据，但不把加载状态暴露进业务 API。
- reload 尽量保持原对象引用不变。
- Texture、Material、Mesh 的对外身份属于 Assets 业务 API；序列化 schema 是生成和加载方式，不应成为 renderer 所依赖的业务类型层级。
- 不建立 AssetTexture → RuntimeTexture → Graphics.Texture 一类重复长期对象链。

## Relations
[[Hot-Reload]] · [[Asset-Update-Boundary]] · [[Texture]]
