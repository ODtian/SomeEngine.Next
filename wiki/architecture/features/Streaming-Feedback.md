# Streaming Feedback

Streaming feedback consumer 是独立 feature/policy。Geometry pipeline 可以产生 fault、usage 或 high-water 数据，但如何解析、预算和调整未来配置由上层决定。

参见 [[architecture/rendering/Delayed-Feedback]] · [[architecture/cluster-streaming/Page-Faults]] · [[architecture/cluster-streaming/Residency-Usage]]。