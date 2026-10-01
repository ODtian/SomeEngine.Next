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
派生声明通过弱引用关联 shader，使其生命周期跟随原 shader 数据。
