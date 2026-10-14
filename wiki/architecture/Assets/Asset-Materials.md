# 材质资产、强类型参数与继承

`MaterialAsset` 提供语义身份，`MaterialBlock` 保存父材质、Shader 引用以及明确的参数覆盖。材质资产家族共用 `AssetId<MaterialAsset>`。业务在 `CreateAsync<TMaterial>` 指定具体 C# 类型，并持有返回的实际对象。

Standard、Unlit 的 C# 声明、配套资产类型与共享 Slang 实现由引擎提供。生成的强类型参数入口访问 MaterialBlock，例如 `StandardMaterialParameters`。项目的具体材质声明也由 MaterialGen 生成 `IMaterial<TMaterial>.Create`；静态实现直接构造指定类型，使用类字段默认值并应用参数。

标准材质对象位于 Resources，资产类型与参数入口位于 Assets 的 Schema。标准材质的图片默认值来自实际 TextureAsset 与材料模板。DefaultGame 的 Standard 模板让 AlbedoMap 和 ARMMap 引用同一个白图，让 NormalMap 引用平法线图；Unlit 模板引用同一个白图。标量默认值由具体材料类的字段初始化表达。

```csharp
await using var loader = new AssetLoader(storage, slang, sessionDescription);
using MaterialAsset asset = await MaterialAsset.OpenAsync(loader, materialId);
using StandardMaterial material = await asset.CreateAsync<StandardMaterial>(loader);
material.Roughness = 0.25f;
```

## 局部覆盖与父级合并

每个参数记录一项局部覆盖。父资产先提供完整参数集合，当前资产逐项替换对应参数；类字段初始化提供其余值。显式零值是覆盖，显式空纹理引用是清除继承纹理。Reset 移除覆盖事实，重新遵循父级或类字段默认值。HasOverride 查询该参数的局部记录。

父级合并发生在 `CreateAsync<TMaterial>` 的明确加载边界，具体类型在生成代码中解释最终参数。父链循环在解析时报告资产数据错误。

## Shader 和引用

MaterialBlock 保存 Shader 资产引用，Material 保存实际 Shader 对象引用。Shader 输入组合见 [Shader 资产](Asset-Shaders.md)。对应管线通过共同 Shader 取得自己的入口，材质解释自己的字段和曲面。

直接依赖来自 MaterialBlock 中的父材质、Shader 以及显式纹理引用。继承后实际使用的资源集合由加载结果表达。

## 创建、更新和释放

资产侧完成父级解析、覆盖合并和资源取得，然后通过静态入口返回完整具体材料。AssetLoader 在构造时接收 Slang 全局会话与一份编译设置，`GetShaderAsync` 和 `GetTextureAsync` 按资产身份共享它创建的实际对象。不同 Material 实例保存这些普通引用，MaterialId 区分业务材料实例。

业务控制 Material 的使用期，AssetLoader 控制其创建的共享 Texture 的使用期。材料释放结束该业务实例的使用；加载器释放等待资源创建结束并关闭共享 Texture。Shader 通过普通 GC 引用保存 Slang 对象与代码。

`UpdateAsync<TMaterial>` 从完整资产内容创建同类型替换字段，再写入已有实例。业务修改继续经过 Game world → Rendering world 的既有提取与资源完成机制。

2026-10-14 行为证据：MaterialCreationBehaviorTests 的消费者静态创建检查通过；MaterialFileTests、MaterialReloadTests 与 MaterialInheritanceTests 的 9 项检查通过，覆盖父级合并、局部覆盖、模板像素、实际 Shader/Texture 共享和业务实例使用。

实现：[MaterialAsset](../../../src/SomeEngine.Assets/Schema/MaterialAsset.cs)、[MaterialBlock](../../../src/SomeEngine.Assets/Schema/MaterialBlock.cs)、[标准材质](../../../src/SomeEngine.Resources/StandardMaterial.cs)、[材质生成器](../../../src/SomeEngine.Generators/MaterialGen.cs)。
