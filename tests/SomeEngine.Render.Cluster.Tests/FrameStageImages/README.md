# Frame stage image baseline

The compressed images contain the 64×64 RGBA16F texture bytes produced by the dependency
commit `1db4de5f13a248377ae6c152f2a51bb0bf352c88`, before the frame stage migration for #73.
Capture used NVIDIA GeForce RTX 3080, driver `32.0.16.1074`, .NET `10.0.12`, Release,
the DefaultProject content built from this commit, and 24 completed frames per image.
The scene is defined in `FrameStageScene`: two sphere instances form a receiver and an
occluder, a fixed point light illuminates the Standard material, and the camera stays fixed.
Full Cluster shading uses the hardware adapter's SM 6.6 and indirect command support.

Each `.rgba16.gz` file expands to 32,768 bytes, with a 512-byte row pitch. The filename
records the backend and the VSM enabled value. D3D12 and Vulkan produced identical bytes
for each VSM state on the capture adapter. Tests compare every byte of the texture.

| VSM | Uncompressed SHA-256 |
| --- | --- |
| Disabled | `b279fdb460f5891f0102a8bc72a84f99a5fde0b646e9b3ec43d1c77f58865304` |
| Enabled | `162fb02835e7e50158f856bc5fdfc3bf6288e51f98c13adae04ecbc0e24c394a` |

Run the image and material behavior checks with:

```powershell
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --filter FullyQualifiedName~FrameStageRenderingTests
```

The material checks read the Temporal output with a camera cut so each image reflects
the current material. Both replacement directions are compared with a fresh installation
on the same hardware adapter and backend.
