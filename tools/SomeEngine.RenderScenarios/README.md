# Rendering scenarios

Build the runtime, then generate a scenario in a new or empty directory outside its build output:

```powershell
dotnet build src/SomeEngine.Runtime/SomeEngine.Runtime.csproj
dotnet run --project tools/SomeEngine.RenderScenarios -- `
  F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Runtime/bin/Debug/net10.0 `
  F:/render-scenarios/displacement displacement
```

The generator copies the built runtime, creates scenario geometry/materials, and cooks the affected shader to DXIL and SPIR-V. It refuses a nonempty destination. It does not change the source runtime or project assets. To refresh code or shaders, generate another output from the newly built runtime.

| Mode | Contents |
|---|---|
| `phong` | One Phong-tessellated sphere |
| `factors` | Four meshes with different source subdivision densities |
| `materials` | Four Phong spheres using different alpha-mask textures and materials, with compatible split stages |
| `wpo-alpha` | Eight displaced/checker-alpha spheres at near and far distances, producing both HW and SW work in hybrid mode |
| `displacement` | Four spheres with an 8×8 bilinear-repeat height map and conservative min/max bounds texture |
| `sparse-motion` | 128 spheres; only one instance moves |

Run from the generated directory:

```powershell
./SomeEngine.Runtime.exe --frames 24 --hidden-window --no-vsync --gpu-validation --verify-frame-output --static-scene
./SomeEngine.Runtime.exe --frames 24 --hidden-window --no-vsync --gpu-validation --verify-frame-output --static-scene --hardware-raster
./SomeEngine.Runtime.exe --frames 24 --hidden-window --no-vsync --gpu-validation --verify-frame-output --static-scene --hardware-raster --no-mesh-shaders
./SomeEngine.Runtime.exe --frames 24 --hidden-window --no-vsync --gpu-validation --verify-frame-output --dynamic-scene
```

`--hardware-raster` selects the full hardware path even on atomic64-capable devices. Hardware raster uses mesh shaders when the device supports the required limits; `--no-mesh-shaders` exercises the VS/PS path, including indexed tessellation. Omit `--hardware-raster` for normal HW/SW selection. Register-cached software Dice requires a 32-lane compute wave; the current shaders explicitly request it. Dynamic motion uses wall-clock time; separate dynamic runs are not synchronized image comparisons. Static captures should compare raw scene depth, visibility and motion as well as color: a full VSM fine-page pool can choose different resident pages across separate executions.

Read `tessDropped` and `tessLimited` independently. The former counts capacity loss; the latter reports reaching the quality/depth budget. A frame with no validation errors can still report a quality limit.

`shadeBinTiles` counts full 8×8 tiles in the first material bin. `shadeItems` counts all emitted tile/quad work records, not pixels or shader invocations. A full tile record represents 64 pixels; a quad record carries a mask for one to four pixels of one material. Both records use 8 bytes. Hardware linear-repeat filtering is used for displacement height; analytic derivatives and conservative error bounds account for its finite interpolation precision.

These are ordinary optional validation tools. See the [implementation and measurement record](../../wiki/architecture/Render-Enhancement-Optimization-2026-09-05.md) for measured cases and limits.
