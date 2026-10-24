# 强类型界面动画 HUD 测量配置

本配置在首次 HUD 基准执行前固定，作为 #52 的代表场景与判定预算。

| 配置 | 数值与边界 |
| --- | --- |
| 设备 | Intel Xeon E5-2698B v3 2.00 GHz，NVIDIA GeForce RTX 3080，驱动 32.0.16.1074 |
| 发布 | win-x64，.NET 10 Release，普通计时与代码生成诊断分开执行 |
| 呈现 | 1280×720，D3D12，Immediate present，允许 tearing，实际增量由相邻帧时间取得 |
| 负载 | 64 个卡片；每个卡片的背景色、圆角和引用 State 叶值持续重定目标；每 16 帧改变位置并执行 FLIP |
| 声明 | 首次建立全部通道、Linear Tween、CubicOut Tween 和 FLIP；后续重复使用同一实体及组件集合 |
| 求值 | Game world 中的公开 Views/Animation/Layout 系统；CanvasRenderExtractor 提取到 Rendering world，再经持久 RenderGraph 与 RHI |
| 预热与样本 | 128 帧预热、512 帧采样，分别记录首次成本和保留内存 |
| 动画子预算 | 动画阶段 CPU P95 ≤ 1.00 ms/帧 |
| 整帧目标 | 持续数百帧；300 Hz 对应约 3.33 ms/帧，报告完整 CPU、GPU 和实际 Present 调用间隔 |
| GPU 边界 | 同一 Graphics Queue 的前后 timestamp，包含当帧画布图提交，等待完成后读取 |
| 分配 | 动画阶段、完整 CPU 帧分别记录当前线程托管分配；首次声明及容量成本单列 |

公开行为验证与性能采样分别运行。原始结果保存公共 FIFO 任务编号、日志、退出码和配置，报告按本预算对照实际数值。
