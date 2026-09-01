# Heuristics  [[architecture/ECS-Architecture|ECS Architecture]]


所有 heuristic 只选择 granularity，不改变可见语义：

- sync Execute 与 parallel dispatch：比较测得的 submit/steal 成本和 per-row/chunk cost estimate；
- entity batch 数：由 stable chunk count、worker count 和测得的 target grain 决定；
- hierarchy split：保留一条 local chain，sibling outbox 达到测得 batch grain后发布；
- hierarchy canonical forest validation（structural transaction或Deferred Parent owner）：small affected set用ancestor-walk，large/reparent-heavy overlay用dynamic-tree index，按测得operation count/depth cost切换；
- exact range 与 whole-family fallback：比较 canonical interval count/frontier cost，fallback 必须计数并可在 profiler看见；
- deterministic reduction：固定 partition count 和 merge tree，不自适应 worker timing；
- dirty full scan：只用于当前结构的损坏诊断/不变量验证，不是正常自适应路径。

阈值由 flat、wide、deep、many-roots、sparse-dirty、dense-dirty、zero-worker 和跨 World benchmark harness 固定。实现前必须报告公式、默认值和基准证据，禁止隐藏 magic number。

