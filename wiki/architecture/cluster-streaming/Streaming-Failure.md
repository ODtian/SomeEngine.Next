# Streaming Failure

不可读取、校验失败或解码失败的 page 进入 terminal failed state。

## Invariants
- 坏 page 不无限重试。
- 当前最低有效 Bootstrap 继续保持可用时，细节失败只意味着无法进一步细化。
- failure 状态不会建立 same-frame recovery path。