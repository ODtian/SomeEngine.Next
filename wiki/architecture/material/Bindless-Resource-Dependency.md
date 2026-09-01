# Bindless Resource Dependency

MaterialBindless 在写 descriptor 值的同时，必须把 descriptor 背后的真实 Graphics.Texture/Buffer 等声明到实际 consumer pass 的普通 RenderGraph access；values buffer 自身也必须进入该 pass。

Descriptor number 不能被用来反查资源，也不能被视为已经表达 graph dependency。

参见 [[architecture/render-graph/Shader-Resource-Closure]]。