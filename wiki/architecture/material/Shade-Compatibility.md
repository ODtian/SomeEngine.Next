# Shade Compatibility

Shade execution key 表示 final shader entry、specialization、fixed output state 与 stage ABI 等真正需要一致的事实。

不同 Material 在 execution key 相同且 command 上的 direct binding 相同时进入同一个 Shade bin，由一条 dispatch 处理。Material identity、indexed scalar 值、MaterialBindless base 与 descriptor value 不拆 bin；传统 direct binding 真正不同才拆成不同 bin。

参见 [[Stage-Compatibility]]。