# BVH Leaf Patching

BVH 长期 buffer 使用 device-local memory。leaf install/evict 通过 RenderGraph 写操作完成。

旧 traversal 对 BVH 的读取与 leaf patch 是真实 hazard，由 GPU queue dependency 解决；默认不为消除这一个依赖而增加 BVH double buffer、额外 pointer table 或每-frame copy。

参见 [[Page-Install]] · [[Page-Eviction]]。