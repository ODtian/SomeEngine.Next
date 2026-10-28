# RHI-QUE-004 — CommandContext, RecordedCommands and bundles 

A `CommandContext` is created for one Queue type/index and may be an ordinary list or a bundle. Its
recording lifecycle is:

```text
idle -> Begin -> recording -> End/EndBundle or Discard -> reusable idle
```

`CommandRecordingDesc` supplies initial capacities for resource descriptors, sampler descriptors and
captured resources. These are preparation hints, not logical limits.

`End` returns a value-type `RecordedCommands` handle with a sequence-protected payload. Its states
cover executable, submitting, submitted, completed, discarded, Device Lost and caller-disposed
outcomes. Copies share the same terminal sequence. Disposing an unsubmitted handle discards its
native payload; disposing after acceptance does not release GPU dependencies early.

D3D12 可以通过 `TryRepeatCommands` 为上一份已完成的命令列表生成新的单次提交句柄。
这条路径沿已关闭列表重新捕获资源、描述符和参数代际；命令存储继续持有其附件描述符、
普通数据与 shader-visible 堆。下一次 `Begin` 开始新的录制后，原生命令匹配同时比较
命令字节、捕获身份，以及临时 RTV/DSV 的资源、子资源范围、格式和视图维度。
每次被接受的提交都沿队列完成边界保持本次捕获有效。

这两条复用路径分别由 [RepeatCommands](../../../../src/SomeEngine.Graphics.Direct3D12/RepeatCommands.cs)
与 [CommandRecordingReuse](../../../../src/SomeEngine.Graphics.Direct3D12/CommandRecordingReuse.cs) 实现；
临时附件内容由 [CommandCopies](../../../../src/SomeEngine.Graphics.Direct3D12/CommandCopies.cs) 随录制收集。

Bundles are caller-disposed Device children and may contain only the command inventory permitted by
the backend. `ExecuteBundle` captures the bundle and its native dependencies.
^rhi-que-004

