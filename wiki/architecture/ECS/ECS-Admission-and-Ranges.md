# ECS admission 与 range  [[architecture/ECS-Architecture|ECS Architecture]]


所有 ECS owner 通过 per-World preparation sequencer。query 的匹配 chunks 只有在 topology 稳定后才能知道，因此 ECS admission 是一个受约束、可回滚的 prepare/seal protocol：

```text
semantic ready and pins held
  -> sort all Worlds by stable WorldId + generation
  -> acquire their preparation sequencers in that order
  -> atomically register topology guards for every World
  -> wait prior topology writers without holding ResourceManager lock
  -> resolve cached queries against stable topology
  -> derive stable chunk/range sets
  -> atomically seal every remaining ECS/external access
  -> publish owner as admitted and release sequencers
  -> wait data hazards
```

sequencer 只串行 metadata preparation；已运行 work 和 release 不被它阻塞。所有 ECS 入口都经过该 sequencer，后来的 owner 不能在 topology guard 和 range seal 之间插入并制造升级环。resolve、cancel、World Closing 或 seal failure 在同一 cleanup path 撤销 topology guards、sealed accesses、pins 和 domain permits，且 owner从未发布 Ready。

topology guards 和最终 range set 属于同一个 Preparing owner；对其他提交的线性化点是成功 seal。ResourceManager lock 只用于短注册/撤销，固定获取顺序为 sorted domain permits -> sorted World sequencers -> ResourceManager admission lock，等待 predecessor 时绝不持 ResourceManager lock。

`ComponentLookupView<T>`、dense/sparse/indexed component、dynamic-buffer header/payload、relation-edge payload、entity existence/location 和 chunk/meta view 全部隐式带 topology Read。snapshot-only borrow 不读取 live storage，使用 generation capture/pin，因此不进入这条 sequencer。resource-bearing external submission 只要引用 ECS storage，也必须走同一 protocol。

range identity 使用 World-qualified、全局单调且永不复用的 64-bit `Chunk.PersistentIdentity`，fork 保持 identity，capture 的 `StructureEpoch` 拒绝 stale packet；未来 chunk pooling 也必须分配新 identity，不能复用旧 id。它不能使用会因 archetype compaction 改变的 index。默认 query owner 拥有 whole matching chunks；只有生成器或专用 partitioner 能证明不相交时才使用 row ranges。

一个 `ScheduleParallel` query 对全部匹配 ranges admission 一次，然后把固定 stable batches 交给多个 work items。`WorkReleased` 在所有 batches 结束后发布。batch 是内部执行单位，不是 child owner；安全性来自生成的 partition proof，而不是“同 owner 自动安全”。


## Related concepts

- [[Packet-Alias-Proof|Owner 内 packet alias proof]]
