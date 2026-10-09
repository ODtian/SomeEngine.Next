# Shade Compatibility

Shade execution key 表示 final shader entry、specialization、fixed output state 与 stage ABI 等真正需要一致的事实。

不同 Material 在 execution key 相同且 command 上的 direct binding 相同时进入同一个 Shade bin，由一条 dispatch 处理。Material identity、indexed scalar 值、MaterialBindless base 与 descriptor value 不拆 bin；传统 direct binding 真正不同才拆成不同 bin。

用户可在 `MaterialScalars` 类型中用与 Texture 属性同名的 `uint` 字段声明 sampled descriptor index。shader 用该值读取自己声明的 unbounded texture array，并为跨材质索引使用 `NonUniformResourceIndex`。索引沿现有 `MaterialScalarRegion` 上传；同一用户 shader 的不同纹理值可以进入同一个 receiver/color bin。Standard、Unlit 和 glTF 示例使用此资源声明，采样方式与材质参数保持一致。

设备纹理数据持有 descriptor 条目和索引，实际分配更换后再更新对应标量字节。图通过 `PassDefinition.Read(GraphParameterInput, PipelineSync)` 声明该用户字段的真实纹理读依赖，上传、替换、取消和 GPU 完成沿原纹理生命周期维护。直接绑定的 sampler、额外 UAV 和其它用户资源继续参与各自的 command binding key。

参见 [[Stage-Compatibility]]。
