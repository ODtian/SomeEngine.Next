# Frame CPU Overlap

## Definition
GameWorld 下一轮 CPU 工作可以与已经交付给渲染侧的上一轮 CPU/GPU 工作重叠。

## Invariants
- render prepare 完成后 renderer 不再依赖该轮 GameWorld 的活跃访问。
- Game N+1 不需要等待 Render N GPU 完成。
- 需要设备反馈的决策只能作用到未来 frame。

## 应用帧阶段

现行实现入口为 [RuntimeApplication](../../../src/SomeEngine.Runtime/RuntimeApplication.cs) 与
[ApplicationScene](../../../src/SomeEngine.Runtime/ApplicationScene.cs)。

带 `UpdateFrame` 或 `FrameCompleted` 的应用先在窗口线程执行输入、`UpdateFrame`、
`BeforeCameraInput`、相机输入与 `SimulateScene`。普通异步帧随后把 geometry 与 views
更新交给游戏任务，窗口线程同时等待实例与 ImGui 存储的 GPU 完成点。
游戏任务结束后，窗口线程推进设备与图的完成交付，再提取这一帧的来源组件、材质与纹理版本，
进入既有 RenderPrepare 边界。`SynchronousFrames` 与场景脚本在窗口线程完成游戏更新。

ImGui 在调试窗口与 `DrawOverlay` 回调结束后通过 `CompleteDrawData` 定稿绘制数据，
在图帧开始前完成所选代的 buffer 扩容。两代映射 buffer 与字体纹理由 UI 持有，
通过 `RegisterExternalBuffer`、`RegisterExternalTexture` 长期登记到图；扩容时替换对应
buffer 的图登记，图负责保存各资源的提交边界。`Record` 为有绘制数据的帧调用
`AddRasterPass`，将 UI pass 放入呈现后的当帧区域；空绘制帧直接完成 UI 录制。
GPU 提交完成点继续交给 UI 的两代存储复用等待。

帧内呈现完成后，`FrameCompleted` 访问同一份已完成游戏状态；场景重载判断随后执行。
使用下一帧游戏任务的应用在这之后启动未来更新，并在下一轮平台输入前收回任务结果。
整帧 `ElapsedMs` 从当轮入口计时到呈现等待结束；views 与 extraction 的计时对应同一个
`SimulationFrame`。`FrameMeasured.CpuMs` 从当轮入口与该帧游戏任务调度时间中的较早者计时到 Present。

## Relations
[[Extraction]] · [[Delayed-Feedback]]
