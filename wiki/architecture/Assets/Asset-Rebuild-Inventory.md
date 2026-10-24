# 当前资产家族

本页列出当前语义家族，不保留被替代的架构规则或迁移前文件枚举；这些内容由 Git 历史保存。按日期记录的交付和验证事实保留在对应证据页，不能替代当前未关闭项。

| 家族 | 当前数据边界 |
|---|---|
| Texture | `TextureAsset` + `TextureBlock`；`Texture` 保留已取得像素数组和设备资源 |
| Mesh | `MeshAsset` + `MeshBlock` + `MeshMaterialsBlock` + `ClusterGeometryBlock` + `ClusterVertexAttributesBlock`；场景只引用 `MeshAsset` |
| Material | `MaterialAsset` + `MaterialBlock`；项目定义具体材质类型与参数 |
| Shader | `ShaderAsset` + `ShaderSourceBlock` + `ShaderBlock`；`Shader` 保留直接 Slang 对象及代码存储 |
| 管线配置 | `ClusterShadersAsset`、`VirtualShadowMapShadersAsset` 保存角色到 `ShaderRef`，不保存 Shader 数据 |
| World 与启动配置 | 语义 Asset 与具体 Block 分离；Game world 到 Rendering world 数据边界不变 |
| 外部扩展 | 外部程序集通过公开 Asset/Block API 定义自己的语义类型和创建行为 |
| 构建 | `AssetProject`、`ShaderSourceFormat` 与项目自己的构建入口维护身份、依赖和产物 |

Shader 的完整边界见 [Asset-Shaders](Asset-Shaders.md) 与 [Shader Block、驻留与 Slang 重构规范](../shader/Shader-Block-Residency-Refactor.md)。
