# Stage Compatibility

## Definition
Material identity 与 stage execution compatibility 是两个独立概念。

Stage 的最终 bin 表示可以在不改变 device command 上 shader/pipeline 与直接绑定状态的情况下，由同一条 draw/dispatch 执行的一组 work。

## Invariants
- Mesh local material ID 先映射到真实 Material 对象。
- Deform、Raster、Shade 分别计算自己的兼容性 key。
- 同一 Material 的三个 key 可以不同。
- 不同 Material 可以共享某一个 stage key。
- execution key 只包含 final shader entry、specialization、fixed state、stage ABI 等执行事实；Material identity 不进入。
- command binding compatibility 只比较真正需要重新绑定 command state 的 direct Texture/Buffer/Sampler/AS 等最终资源和 view 描述。
- MaterialBindless base、其 values 中的 descriptor index、普通 scalar 值以及其它由 shader 按 material/work index 读取的数据不拆 bin。
- 两个 Material 的 direct binding 不兼容时建立不同 bin，不把这种合法组合当成错误。
- bin index 在 scene/pipeline 更新边界注册并长期复用；新增或删除无关 Material 不重编号仍存活的 bin。frame hot path 只消费已分配的整数，不分配 bin。

## Relations
[[Deform-Compatibility]] · [[Raster-Compatibility]] · [[Shade-Compatibility]] · [[architecture/cluster/Material-Ranges]]

同一 shader 修订版本的固定资源声明按编译目标和最终 entry 组合共享。
资源顺序从链接参数布局解析一次，材质值仍由各 Material 分别解释及绑定。
Shader 更新后按修订版本重新解析声明，旧阶段随原图和管线生命周期处理。
`ShaderParameterLayout` 通过普通引用持有使其反射句柄有效的 Shader；Shade 阶段在图接受材质变化后，按当前着色与阴影请求入口清理退出使用的布局。

Cluster 的设备管线由 `ClusterPipelineSet` 集中持有。Compute 以 Slang 代码与最终入口区分，Hardware 以 Slang 代码及几何、像素入口区分；可见性模式和固定渲染状态由同一管线集合确定。每个 view 的 Raster、Shade 阶段从该集合取得管线，分别维护材质兼容性分组、标量布局与资源绑定。

图接受材质变化后，view 更新实际使用的设备管线集合，`ClusterPipelineSet` 释放全部活跃 view 已退出使用的管线。最后一个 view 结束时保留其最终管线集合，应用在同一设备重载场景时复用现有 Shader 弱值及设备管线；下一次图接受材质变化时再按实际使用范围收敛。Shader 替换沿已有提取和 RenderWorld 材质更新边界触发重新解析，录制与已接受提交通过 RHI 的 native 引用持有实际 GPU 资源。

源码：[ClusterPipelineSet.Materials](../../../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSet.Materials.cs)、[ClusterMaterialStages](../../../src/SomeEngine.Render.Cluster.Pipeline/ClusterMaterialStages.cs)、[ClusterPipelineSystem](../../../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.cs)。
