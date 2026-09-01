# Relation Adjacency

Outgoing、incoming、incident adjacency 与 relation-presence index 都是 immutable generations，由 edge/endpoints 重新构造。

Reader pin 只保护选中的 generation 生命周期，不阻止下一 generation 产生。若需要保证读取某次结构变化之后的数据，调用者仍需依赖对应结构 work completion。