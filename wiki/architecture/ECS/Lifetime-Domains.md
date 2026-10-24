# Lifetime Domain 与 runtime  [[architecture/ECS-Architecture|ECS Architecture]]


domain 状态为：

```text
Open -> Closing -> Closed
```

root scope 在 Open 时取得 permit，并持有到完整 `ScopeCompleted`；descendant 继承 permit。Closing 拒绝新 roots，但允许已有 scope 继续产生 descendants。当前同步 `Dispose()` 等 tracked job root scopes 与 unbound admissions 全部结束后才释放 storage。

一个 job 可同时持有 runtime、World、system 和 module permits。访问另一个 World 需要取得另一个 domain permit；该 World 已 Closing 时 submission 失败。

多 domain/World acquire 和 close 一律按 stable `(domain kind, WorldId/systemId, generation)` 排序；失败时逆序撤销已取得 permits/sequencers。两个跨 World jobs 即使分别以 A/B 和 B/A 声明，runtime也按同一排序准备，不能形成 sequencer deadlock。

runtime hot replacement 不能把 old generation handle 当作 completed。ambient job submissions 路由到 owning scheduler；handle operations 按 generation registry 找 owning runtime；cross-generation dependency 拒绝。旧 runtime 完整 Closed 后才能销毁 workers/resource manager。

当前不提供可取消的 `DisposeAsync`。若未来增加取消等待，取消也只能停止 caller 的等待并报告 outstanding owners，不能强制释放仍被 running body 使用的内存；World 必须保持 Closing。

持有某 domain permit 的 ambient scope 不能同步 close-and-wait 同一 domain，否则它会等待自身。该调用立即拒绝；scope内只能向 lifecycle coordinator请求 close，真正 await 必须在 scope外。system remove只由 coordinator发起。World A job可以关闭未持有 permit 的 World B；一旦它也持有 B permit，同样拒绝同步等待。

