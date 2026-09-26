# 材质资产、强类型参数与继承

2026-09-16。`MaterialAsset` 提供语义身份，`MaterialBlock` 保存材质类型、父材质、Shader 引用以及明确的参数覆盖。材质资产家族共用 `AssetId<MaterialAsset>`。使用对象是普通 Material 及项目的具体材质类型，不依赖资产加载设施。

Standard、Unlit 的 C# 声明、Slang 实现及默认内容属于项目模板。安装后由用户项目维护，引擎更新不会复制覆盖它们。生成的强类型参数入口访问 MaterialBlock，例如 `StandardMaterialParameters`，不恢复旧的嵌套资产字段或通过字符串解释 Block 业务。

## 覆盖不是默认值比较

未填写参数表示不覆盖父级；没有父级时使用具体材质声明的默认值。显式零值是覆盖，显式空纹理引用是清除继承纹理。Reset 移除覆盖事实，重新遵循父级或默认值。HasOverride 查询是否明确填写，而不是检查数值是否等于默认值。

覆盖位是材质内容自身的业务数据，不是字段 COW 计数。父级合并发生在明确的加载边界；查询资产参数不会隐式打开父材质。父链循环与类型不相容必须在解析时失败。

## Shader 和引用

一个 Material 保持一个 Shader 资产引用。项目顶层 Shader 将材质算法与所需管线入口组合；增加其他管线时，不在引擎增加材质名到 Shader 的替换表或自动回退。更换 Material 的 Shader 引用意味着明确更换实现。

当前直接依赖来自 MaterialBlock 中的父材质、Shader 以及显式纹理引用。继承后实际使用的资源集合是加载结果，不混入直接依赖的含义。具体材质类型由静态登记的构造与参数代码处理，不扫描反射或逐字段构建动态操作表。

## 创建、更新和释放

资产侧完成父级解析、覆盖合并和资源取得，然后返回可直接使用的 Material。更新保留对象身份；切换前失败保留旧内容，切换后通知失败不能导致新纹理被临时对象释放。独立创建的两个材质各自持有自己的资源使用期限，Loader 关闭不销毁它们。

渲染仍通过既有 Game world 提取和资源完成机制保护旧帧，不另建材质资产专用提交路径。行为证据见 [本次验收](Asset-Verification-2026-09-16.md)。

实现：[MaterialAsset](../../../src/SomeEngine.Assets/Schema/MaterialAsset.cs)、[MaterialBlock](../../../src/SomeEngine.Assets/Schema/MaterialBlock.cs)、[项目材质](../../../templates/DefaultProject/)、[材质生成器](../../../src/SomeEngine.Generators/MaterialGen.cs)。
