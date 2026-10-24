# 材质资产、强类型参数与继承

`MaterialAsset` 提供语义身份，`MaterialBlock` 保存父材质、Shader 引用以及明确的参数覆盖。材质资产家族共用 `AssetId<MaterialAsset>`。业务在 `CreateAsync<TMaterial>` 指定具体 C# 类型，并持有返回的实际对象。

Standard、Unlit 的 C# 声明、配套资产类型与共享 Slang 实现由引擎提供。生成的强类型参数入口访问 MaterialBlock，例如 `StandardMaterialParameters`。项目的具体材质声明由 MaterialGen 生成 `IMaterial<TMaterial>.Create`；静态实现直接构造指定类型，使用类字段默认值并应用参数。

标准材质对象位于 Resources，资产类型与参数入口位于 Assets 的 Schema。`MaterialTemplates.SaveAsync(project, standardShader, unlitShader)` 保存实际白图、平法线图和两份材质模板。Standard 的 AlbedoMap 和 ARMMap 引用同一个白图，NormalMap 引用平法线图；Unlit 引用同一个白图。项目可通过 `CreateStandard`、`CreateUnlit` 取得模板内容并编辑。标量默认值由具体材料类的字段初始化表达。

## 解析与具体资源创建

`ResolveAsync(files)` 返回独立的完整 MaterialBlock。结果的 Name 来自当前 MaterialAsset，Parent 保留当前直接父资产引用，Shader 与 Parameters 合并全部父级和本地覆盖。结果中的强类型 Shader、Texture 资产引用供业务取得实际资源。名称由 MaterialAsset 保存，解析结果的 Name 随结果分支复制。

业务使用具体 ShaderAsset、TextureBlock 方法创建资源，再向材质创建入口传入两份普通强类型字典：

```csharp
await using var files = new AssetLoader(storage);
using MaterialAsset asset = await MaterialAsset.OpenAsync(files, materialId);
using MaterialBlock content = await asset.ResolveAsync(files);
Dictionary<AssetId<ShaderAsset>, Shader> shaders = [];
Dictionary<AssetId<TextureAsset>, Texture> textures = [];

if (!content.Shader.IsEmpty)
{
    using ShaderAsset shaderAsset = await ShaderAsset.OpenAsync(files, content.Shader);
    shaders.Add(content.Shader, await shaderAsset.CreateAsync(slang, sessionDescription));
}
foreach (AssetId<TextureAsset> id in content.Parameters
    .Where(value => value.Kind == MaterialParameterKind.Texture && !value.Texture.IsEmpty)
    .Select(value => value.Texture).Distinct())
{
    using TextureAsset textureAsset = await TextureAsset.OpenAsync(files, id);
    TextureBlock pixels = await textureAsset.GetBlockAsync<TextureBlock>();
    textures.Add(id, await pixels.CreateAsync(textureAsset.Name));
}

StandardMaterial material = await asset.CreateAsync<StandardMaterial>(files, shaders, textures);
material.Roughness = 0.25f;
```

`CreateAsync<TMaterial>(files, shaders, textures)` 在内部重新解析当前完整内容，按资产引用选择传入的实际资源，然后调用生成的具体类型创建代码。两次创建各自拥有 MaterialId，传入同一 Shader、Texture 时各材质持有相同实际引用。业务可以通过通用弱索引复用存活资源，具体资源创建和引用选择仍由业务表达。

## 局部覆盖与父级合并

每个参数记录一项局部覆盖。父资产先提供完整参数集合，当前资产逐项替换对应参数；类字段初始化提供其余值。显式零值是覆盖，显式空纹理引用是清除继承纹理。Reset 移除覆盖事实，重新遵循父级或类字段默认值。HasOverride 查询该参数的局部记录。

父级合并发生在 ResolveAsync、CreateAsync 和 UpdateAsync 的明确异步边界，具体类型在生成代码中解释最终参数。父链循环在解析时报告资产数据错误。

`GetTemplateAsync(files)` 从材质资产的父关系取得祖先模板身份。glTF Standard、Unlit 导入将模板保存为 `MaterialBlock.Parent`，参数只表达来源覆盖。实际纹理及 Shader 通过解析结果和业务提供的引用取得。

游戏通过 `ISceneMaterials` 的静态 `CreateAsync` 选择具体材质，公开启动为 `Application.RunAsync<GameMaterials>(args)`。`EngineSceneMaterials` 根据 Standard 与 Unlit 模板引用分别调用具体的 `CreateAsync<StandardMaterial>`、`CreateAsync<UnlitMaterial>`。DefaultGame 的 `SampleSceneMaterials` 使用 Imported 模板引用创建实际 `SceneMaterial`，其余材料继续使用引擎模板。

## Shader 和引用

MaterialBlock 保存 Shader 资产引用，Material 保存实际 Shader 对象引用。Shader 输入组合见 [Shader 资产](Asset-Shaders.md)。对应管线通过共同 Shader 取得自己的入口，材质解释自己的字段和曲面。

直接依赖来自 MaterialBlock 中的父材质、Shader 以及显式纹理引用。继承后实际使用的资源集合由解析结果表达。

## 更新与使用期

`UpdateAsync<TMaterial>(material, files, shaders, textures)` 使用与创建相同的明确输入，按完整内容建立替换字段，再通过 MaterialGen 生成的 `IMaterial<TMaterial>.Update` 直接写入现有具体实例。更新保留 MaterialId，并在全部字段写入后将 Revision 推进一次。Reset 后的字段恢复父级与类字段默认值；实际 Shader、Texture 共用关系由本次输入的引用决定。

Material 保存自己的参数、MaterialId、版本与实际 Shader、Texture 引用。业务通过普通引用维持这些对象的使用期，场景卸载、材质替换和依赖变化通过更新自身引用结束持有。AssetLoader 持有文件访问与读取状态，文件访问结束后，已创建材料和资源继续依靠实际对象引用使用。

Shader 的显式更新通过具体 ShaderAsset 方法取得新的完整 Shader，再交给材质更新入口。Texture 的显式更新通过 TextureBlock.UpdateAsync 修改指定实际 Texture，维持已有消费引用。字段版本和材料 Revision 沿 Game world → Rendering world 的既有提取和资源完成机制传递，见 [更新边界](Asset-Update-Boundary.md)。

2026-10-16 行为检查覆盖完整解析的独立结果、父级覆盖、显式纹理清除、实际资源引用、独立材质身份、更新身份与一次 Revision、父级及类默认值恢复，以及消费者程序集的自定义材料创建和更新。

2026-10-14 集成证据：SharedMaterialEntryTests 从同一个 StandardMaterial 经既有提取与公开 GPU 参数绑定，验证两个入口组在业务参数修改前后的实际读回结果，覆盖 IR 和 CLI 保存的共同代码。资产 NativeAOT 原生程序完成下游自定义材料的静态创建、更新、默认值恢复与共享 Shader 使用。

实现：[MaterialAsset](../../../src/SomeEngine.Assets/Schema/MaterialAsset.cs)、[MaterialBlock](../../../src/SomeEngine.Assets/Schema/MaterialBlock.cs)、[标准材质](../../../src/SomeEngine.Resources/StandardMaterial.cs)、[材质生成器](../../../src/SomeEngine.Generators/MaterialGen.cs)。
