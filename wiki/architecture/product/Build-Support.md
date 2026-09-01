# Build Support

`tools/`、source generators、samples、benchmarks 与 test projects 可以参与资产生成、代码生成、性能证据和 conformance，但不因此成为 shipping engine surface。

Build-support code 可以依赖 compiler/tooling 能力；shipping code 不应为了工具链方便携带只在 cook/build 阶段需要的 compiler state。

参见 [[architecture/shader/Shader-Build]]。
