# Raster pipeline fixtures

`triangle.vs.dxil` and `triangle.ps.dxil` are offline-cooked Shader Model 6.2 artifacts for the
native WARP pipeline-state regression test. They are deliberately checked in so the RHI test does
not compile shaders at runtime.

Regenerate them from `triangle.hlsl` with the repository-pinned DXC binaries:

```powershell
external/dxc/bin/x64/dxc.exe -T vs_6_2 -E VSMain -Fo tests/SomeEngine.Graphics.Direct3D12.Tests/Fixtures/triangle.vs.dxil tests/SomeEngine.Graphics.Direct3D12.Tests/Fixtures/triangle.hlsl
external/dxc/bin/x64/dxc.exe -T ps_6_2 -E PSMain -Fo tests/SomeEngine.Graphics.Direct3D12.Tests/Fixtures/triangle.ps.dxil tests/SomeEngine.Graphics.Direct3D12.Tests/Fixtures/triangle.hlsl
```
