# Tessellation

Tessellation 按 Nanite 风格作为 Raster execution 的一部分，不增加独立 Tess bin。

用户 surface 接口同时定义 forward position/evaluation 与 shade reconstruction；两者在 shader build/link 时静态组合。不把 displacement 写死成 height texture，也不要求所有 surface 共用一个 inverse 算法。普通 linear surface、normal scalar displacement、texture/procedural displacement 与 Phong tessellation 都可实现同一接口，但各自选择适合自身数学结构的 `reconstruct`。

参见 [[Tessellation-Reconstruction]] · [[Raster-Execution]]。
位移范围纹理的公开构建、逐层归约和材质采样入口见 [[Displacement-Textures]]。
