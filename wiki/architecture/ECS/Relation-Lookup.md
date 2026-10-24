# Relation Lookup

长期 lookup description 只声明未来需要的 topology access；进入执行 scope 后取得 generation-pinned read-only lookup view。

Directed relation 提供 endpoints、outgoing、incoming；undirected relation 提供 endpoints、other endpoint、incident edges。Payload random access 通过普通 component lookup 独立声明，避免 adjacency read 与 payload writer 产生假冲突。

Ordered enumeration 只在对应 shard 真正具有 ordered capability 时提供，不能把 packed storage 顺序提升成业务顺序。