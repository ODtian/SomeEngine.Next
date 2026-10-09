# Build Support

`tools/`、source generators、samples、benchmarks 与 test projects 可以参与资产生成、代码生成、性能证据和 conformance，但不因此成为 shipping engine surface。

Build-support code 可以依赖 compiler/tooling 能力；shipping code 不应为了工具链方便携带只在 cook/build 阶段需要的 compiler state。这个边界不禁止用户机器上的正式 Shader 编译：当应用选择 `ShaderSourceBlock` 时，Slang 编译就是该 Block 的运行时数据取得路径，所需 Slang 对象由最终 `Shader` 直接持有，而不是 cook 工具状态泄漏到 shipping surface。

参见 [[architecture/shader/Shader-Build]]。

## 开发依赖文件

普通 `dotnet build` / `dotnet test` 在原有输出位置为 SDK 已解析的 `ReferenceCopyLocalPaths` 建立符号链接，不扩张依赖集合，也不按后缀扫描文件。项目显式声明的原生依赖 Content 使用 `DependencyBinary="true"`，该元数据随项目引用传递。固定 TestHost/xUnit 包以 Content/None 声明的必要宿主和适配器文件也显式标记；其它 Content 保留原有复制行为，包括可写配置和资产。

链接操作使用 MSBuild 的标准 Copy 任务，失败即构建失败，不回退复制、不调整系统权限。相同目标的既有链接不重写；旧普通副本和依赖来源变化会转换为当前链接。构建要求仓库固定的 .NET SDK MSBuild 宿主和逐项目输出目录；链接只引用当前依赖项给出的源路径，不创建跨工作树共享输出目录。开发输出依赖原文件存在，不能当作部署包直接搬走。

正式 `dotnet publish`（包括 `--no-build`）仍从 SDK 的完整发布项产生独立普通文件，禁止发布链接选项和覆盖源输出。已有发布符号链接先解除，再由 SDK 复制内容；引擎的加载路径和运行时代码不变。

`tools/Verify-DependencyLinks.ps1` 可核对每个项目 `obj/<配置>/<TFM>/development-dependency-links.txt` 中实际选中的源文件与输出链接；行为回归位于 `DependencyLinkCommandTests`。
