# Job Command Production

Job-side structural work 使用 job-scoped command segments，而不是可逃逸的普通 CommandBuffer writer。

每个 producer key 对应私有 segment；producer callback 结束后 segment 封闭。CreateEntity 返回 segment 内 deferred identity，真正 Entity 在 deterministic merge/playback 时分配。

Playback 依赖所有 producers 成功结束；任一 producer 或其后代失败时整批不执行 partial playback。