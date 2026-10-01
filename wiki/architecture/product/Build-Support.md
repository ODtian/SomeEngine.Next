# Build Support

`tools/`、source generators、samples、benchmarks 与 test projects 可以参与资产生成、代码生成、性能证据和 conformance，但不因此成为 shipping engine surface。

Build-support code 可以依赖 compiler/tooling 能力；shipping code 不应为了工具链方便携带只在 cook/build 阶段需要的 compiler state。这个边界不禁止用户机器上的正式 Shader 编译：当应用选择 `ShaderSourceBlock` 时，Slang 编译就是该 Block 的运行时数据取得路径，所需 Slang 对象由最终 `Shader` 直接持有，而不是 cook 工具状态泄漏到 shipping surface。

参见 [[architecture/shader/Shader-Build]]。
