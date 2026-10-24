# External work  [[architecture/ECS-Architecture|ECS Architecture]]


已有 fence 只能创建无资源 completion bridge。把 resource access 附加到已经开始的 GPU/IO work 无法证明安全，因此 resource-bearing external API 必须是 submission transaction：

```text
pin and semantic wait
  -> admit external owner
  -> wait prior hazards
  -> invoke provider.Submit on required thread
  -> atomically bind returned fence
  -> release owner only on signal/fault/device-lost
```

submit 抛错必须释放 owner/pins并 fault handle。external fault 不阻止纯 hazard successors，但显式 successors 收到 fault。shutdown 遇到永不 signal 的 fence 保持 Closing 并报告 fence/resource/owner，不能提前释放。

