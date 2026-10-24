# Directed and Undirected Relations

Directed relation 暴露 Source/Target 与 outgoing/incoming adjacency。

Undirected relation 只保存一条 edge，保留 EndpointA/EndpointB 两个 endpoint-local role，并在两端建立 incident adjacency；不物化 A→B/B→A 两份 truth，也不因 unordered-pair uniqueness 自动交换 endpoint-local payload。

具有施加者/接受者等非对称角色的 relation 必须使用 directed 语义。