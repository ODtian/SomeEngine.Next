# Page Faults

Page fault 表示 traversal 遇到 nonresident Page ID 后产生的缺页请求。

## Invariants
- fault queue 是 fixed-capacity worklist。
- overflow 可以直接丢 fault；页仍保持 nonresident，未来 traversal 自然再次产生 fault。
- 不建立 guaranteed replay / acknowledge 协议。
- fault 与 [[Residency-Usage]] 是两套信息。
- fault readback 只影响未来 frame。