# Scene Scripts

`Application.RunAsync(args, SceneScript)` runs a finite scene through the public application entry point. The script selects a content directory, frame count and ordered frame actions. Its `ApplicationResult` reports the completed frames and a typed failure category.

Scripts use a hidden window, a fixed time step and a software adapter by default. `--hardware-adapter` selects the normal adapter. Startup options continue to select the graphics backend, scene behavior, temporal antialiasing and shadows.

`GeometryMemoryBytes` configures the Cluster page heap. `WaitForFrames` controls completion waits. Frame actions run before that frame's Game world extraction, including when submitted frames overlap.

## Frame actions

`SceneAction.Frame` is zero based. Actions for the same frame preserve their input order. Each action receives `SceneEdit` with the following operations:

| Operation | Public result |
| --- | --- |
| `MeshEntities` | Current mesh entity identities |
| `SetOutputSize` | Presentation client dimensions |
| `SetCameraTransform` | Camera position and orientation |
| `GetMaterial` / `ReplaceMaterial` | A mesh entity's material binding |
| `AddEntity` / `DeleteEntity` | Mesh instance creation and removal |
| `CaptureImage` | The completed frame's packed RGBA8 pixels |

`SceneImage` holds its pixel array through an ordinary reference. `SaveBmp` writes the retained image as a top-down bitmap. The capture path waits for its GPU copy and delivers the image to each callback registered for that frame.

The Runtime test helper `SceneImages` compares complete images, regions and colors. An assertion failure writes actual, expected and difference PNGs to `SOMEENGINE_TEST_ARTIFACTS`, following [Behavior Checks](../../harness/Behavior-Checks.md).

Sources: [Application](../../../src/SomeEngine.Runtime/Program.cs), [SceneScript](../../../src/SomeEngine.Runtime/SceneScript.cs), [RuntimeApplication](../../../src/SomeEngine.Runtime/RuntimeApplication.cs), [SceneImages](../../../tests/SomeEngine.Runtime.Tests/SceneImages.cs).
