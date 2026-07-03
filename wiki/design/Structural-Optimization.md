# Structural Optimization

性能目标必须靠**跨场景成立的结构性优化**达成。

禁止通过调场景参数、benchmark 负载、pass 开关、功能降级、validation/static/placed/alias/细粒度 pass 关闭、warmup/window cutoff 调整等**换个场景就失效的调参方式**凑结果。

结构性优化的判据：换场景不失效。

参见 [[DRY]]、[[SRP]]（结构性优化的基础是关注点清晰）。
