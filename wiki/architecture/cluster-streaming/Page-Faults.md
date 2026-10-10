# Page Faults

Page fault 表示 traversal 遇到尚未可用的 hierarchy part 后产生的页请求。该 part 自身的页可能已驻留，但同组或粗层依赖尚未齐全。

## Invariants
- fault queue 是 fixed-capacity worklist。
- 请求先补齐页范围、reduction group 和祖先依赖，再逐页判断是否需要读取。不能因生成请求的 part 自身页已驻留而丢弃整条请求。
- overflow 可以直接丢 fault；缺失依赖仍使该组不可用，未来 traversal 自然再次产生 fault。
- 不建立 guaranteed replay / acknowledge 协议。
- fault 与 [[Residency-Usage]] 是两套信息。
- fault readback 只影响未来 frame。
## Delivery

Cluster 的反馈区段通过图持久回读读取有效页缺失队列范围。录制标签为驻留 epoch，
接收者只保存队列使用的字节；流送在之后的 prepare 消费完成反馈。具体完成边界、
消费统计与帧标签见 [[architecture/rendering/Delayed-Feedback]]。
