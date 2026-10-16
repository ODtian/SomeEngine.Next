# 材质贴花

`MaterialDecal` 与实体的 `WorldTransform` 描述一个投影层。变换把局部 `[-1, 1]` 盒映射到世界中的影响体积，`Material` 持有用户参数与资源，`Order` 决定由小到大的组合次序。相同次序使用实体索引排序。

`MaterialDecalRenderExtractor` 将该引用、次序与变换交付到 Rendering world 的 `RenderMaterialDecal` 和 `RenderTransform`。Cluster 从该集合构建 GPU 体积与属性数据，网格实例继续沿既有实例提取。`SceneEdit.AddMaterialDecal`、`SetMaterialDecal`、`SetEntityTransform` 与 `DeleteEntity` 提供相同的游戏世界编辑入口。

## 用户属性与受光

用户 include 通过 `[MaterialDecalScalars]` 声明贴花属性的字节布局。`ScalarLayout` 按字段名称读取 `Material` 的属性，并把纹理与 sampler 写成设备描述符索引。GPU 体积携带参数地址，属性内容由用户 shader 解释。

DefaultProject 的 `coating_material.slang` 声明 `CoatingScalars`、纹理/sampler 数组及 `EvaluateCoating`。该层复用 `StandardMaterial` 的颜色、法线图、ARM 图、粗糙度与 sampler 参数，按体积局部 XY 采样，并把法线变换到世界方向。Standard 和 Unlit 的 `evaluateSurface` 都在自己的最终命中位置读取同一参数与资源视图。Standard 将层组合到基础色、着色法线及粗糙度，Unlit 组合到颜色；表面自身的 alpha 持续表达该覆盖命中的透明度。

组合发生在表面求值中。opaque 的 Forward/Deferred 路径和每个透明命中均获得自身的层属性，随后执行原有求色与阴影采样。`SurfaceReceiver.GeometricNormal` 持续表达原表面几何，贴花混合后的法线作为 `ShadingNormal` 进入光照。

## Forward+ 候选

贴花与光照共用 `ClusteredViewGrid`。Count 投影体积的八个角点，按相交 XY tile 与视空间 Z 切片计数；Reserve 保留连续引用区间；Scatter 写入体积索引；Sort 恢复调用方指定的次序。Clear、Count、Reserve、Scatter 和 Sort 在持久图中保留固定阶段。视图或体积输入改变时刷新，相同输入复用 cell 内容。

每个命中根据最终位置查询自己的 cell，再以体积的逆变换检查局部范围。引用容量决定驻留区间；其余 cell 按相同次序遍历实际体积来源。`--blend-index=full|xyz` 提供完整来源与 cell 查询的对照。

## DefaultGame 场景

- `decal-layers`：16 组局部布置，每组包含一面 opaque 墙、三个不同深度的透明面和 16 层涂层，共 256 个体积。
- `decal-sparse`：64 个局部弹孔体积作用于 opaque 墙；前方透明面位于体积之外的深度。

两组均通过公共场景编辑更新体积变换。涂层使用 `Blend coating normal` 和烟雾 alpha 纹理显示法线、粗糙度与空间覆盖。

公共图像检查从 `Application.RunAsync` 与 `SceneScript` 进入整条提取和绘制路径，比较已知的两层颜色次序。图像失败沿共享检查保存实际、期望及差异 PNG。
