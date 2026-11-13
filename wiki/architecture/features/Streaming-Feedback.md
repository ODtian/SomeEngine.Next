# Streaming Feedback

流送策略消费几何管线产生的 fault 与可见需求，决定未来帧的页预算和驻留集合。

渲染侧将纯值 `ClusterPageDemand` 写入共享的 `RenderFeedback<ClusterPageDemand>`。记录分别表达页故障、可见页优先级、视图结束和本帧实际读回数量，携带产生帧号与 Cluster epoch。GPU 的八字节页需求在回读入口解析为队列记录；流送算法继续使用自己的紧凑 `ClusterPagePriority`。

`ClusterStreamingFeedbackConsumer` 按图的 `LastCompletedFrame` 提取队列记录，并按 epoch 与帧号合并多个视图。相同页取最高优先级，完整需求标记由各视图共同决定。全部视图到达后，消费方立即把故障与合并需求交给页流送，维护反馈序号和统计。

同一视图的记录以共享队列的批量写入提交。消费方保存尚在合并的帧数据，已合并帧直接交给页流送。

参见 [[architecture/rendering/Delayed-Feedback]]、[[architecture/cluster-streaming/Page-Faults]]、[[architecture/cluster-streaming/Residency-Usage]]。
