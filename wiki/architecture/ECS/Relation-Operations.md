# Relation Operations

Topology surface 以 edge identity 为单-edge修改依据：create、destroy、retarget。`(source,target)` 只用于 lookup 或名字明确表示 bulk 的操作。

Retarget 保持 edge identity、payload 与附加 components。Primary relation kind/endpoints 不能通过普通 component Add/Remove 伪造；payload 在 endpoints 不变时仍是普通 edge component。