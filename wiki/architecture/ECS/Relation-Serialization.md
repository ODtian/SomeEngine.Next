# Relation Serialization

序列化保存 edge identity/endpoints/payload，以及 ordered shard 的 semantic edge sequence 与 empty ordered policy；不保存临时 gap/order allocator 等内部历史。

Load 后重建 adjacency/index generations 与内部 order metadata。Unordered export 如需 deterministic output，可在导出 scratch 中按 edge identity 排序，live unordered adjacency 不为此持续付费。