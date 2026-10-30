# DXC

Game projects copy the host's DXC libraries to the application directory through
`build/SomeEngine.Game.targets`. Slang uses that directory for its downstream DXIL
compiler. Development output follows the existing dependency links; published
applications include the libraries.

The Linux x64 libraries in `lib/linux-x64` come from Microsoft's
[DXC v1.8.2407 release](https://github.com/microsoft/DirectXShaderCompiler/releases/tag/v1.8.2407),
archive `linux_dxc_2024_07_31.x86_64.tar.gz`. The distribution licenses are included
alongside those libraries. This version supports the Linux shader import path on
Ubuntu 22.04, including generation of DXIL and SPIR-V targets through Slang.
