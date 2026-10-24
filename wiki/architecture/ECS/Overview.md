# 结论  [[architecture/ECS-Architecture|ECS Architecture]]


SomeEngine.Next 使用四个相互独立的正确性概念：

1. **Semantic Dependency** 选择必须观察的前序结果，等待完整 scope，并传播 fault/cancel。
2. **Resource Owner** 保证已 admission 的访问不重叠，等待前序 work release，不传播业务 fault。
3. **Job Scope** 包含 body 和所有动态 descendants，决定 handle 何时真正完成。
4. **Lifetime Domain** 决定 runtime、World、system/module 何时可以停止接收 roots 并安全释放。

`Schedule`、`Execute` 和 external submission 是同一个 ResourceManager 的三类 owner：

```text
Schedule          async CPU owner
Execute           caller-thread synchronous owner
External submit   externally-completed owner
```

ECS 业务代码不能构造 World 的 raw `JobResourceAccess`。query、generated job、typed lookup 和 command writer 是唯一入口。

