# 资产重构结果

本页只保留当前职责与结果导航；被替代的架构规则由 Git 历史保存。带日期的执行和验证证据保留其范围，不作为当前 API 说明。

当前资产边界、公开行为与验证入口分别见：

- [资产文件架构](../Asset-Files.md)
- [2026-10-01 缓存与规范对齐验证](Asset-Verification-2026-10-01.md)
- [ShaderAsset、Block 与驻留 Shader](Asset-Shaders.md)
- [Shader Block、驻留与 Slang 重构规范](../shader/Shader-Block-Residency-Refactor.md)
- [2026-09-30 托管存储与数据共享验证](Asset-Verification-2026-09-30.md)
- [2026-09-16 原任务验证](Asset-Verification-2026-09-16.md)
- [2026-09-29 实现审查与未关闭验收](../../../reviews/AssetSpecReview-2026-09-29.md)

Shader 当前只有 `ShaderAsset` 语义身份，使用 `ShaderSourceBlock` / `ShaderBlock` 两种异步数据边界创建同一种驻留 `Shader`。反射来自直接 Slang 对象，旧反射副本、旧 schema 读取与本地 Slang fork 均不属于现行实现。

普通 Block 字段和发布字节采用托管数据。整 chunk、纹理 tile、几何页和 BVH 通过 AssetByteCache 共享最终数组和进行中的读取，整数 LRU 记录及动态查询索引共用 CPU 字节预算。文件读取直接写最终数组，旧 CPU 页容器、页级 COW 和每文件解码缓存已移除，具体规则见 [数据共享与修改](Asset-Sharing-and-Writes.md)。
