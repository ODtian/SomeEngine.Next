# Default project

Install this template once into an absent or empty directory. The copied C#, shader definitions, material implementations, pixel defaults and model are project sources. Updating the engine never copies them over an existing project.

```powershell
pwsh -File <engine>/templates/DefaultProject/Install.ps1 -Destination <project> -EnginePath <engine>
dotnet run --project <project>/Game.Build/Game.Build.csproj -c Release -- --project <project> --engine <engine>
dotnet run --project <project>/Game.App/Game.App.csproj -c Release
```

`DefaultGame` opens the scene test platform. Its SomeUI panel appears at the lower right:
click `Add one` to increase `Clicks: N`, or use an arrow key to focus the button and Enter or
Space to confirm it. The count stays with the panel when the window is resized.
F1 opens and closes the ImGui debug panel, where `Surface panel` controls the SomeUI example.
The application takes its scene and rendering settings from that in-game panel.

`Game.Build` compiles every project `.shader`, creates the default textures and materials from the declarations in `DefaultTextures.cs` and `DefaultMaterials.cs`, and imports the copied glTF model through the project's importer capabilities. The glTF importer produces the source scene and shared mesh assets. `DefaultContent` then builds the separate 1024-instance test world from that mesh and writes application settings pointing to the test world. `DefaultContentIds.cs` holds identities for code-defined assets; changing a display name keeps its identity. The glTF source has its own persisted import identities and settings after the first import.

`StandardGltfSceneImporter` is the ordinary glTF scene importer for this project. It assembles the scene through the shared base class and directly builds this project's Standard and Unlit materials. A different scene importer can replace it when a scene needs different material or world content. The selected importer requests shared mesh assets; mesh materials and textures are reused by source identity. Saved worlds contain mesh references and node transforms, while each mesh asset holds ordered material references.

The build reads the engine's `assets/Shaders` directory as an include library. Project `Pipeline/build_*.slang` files select general engine entries. `standard.slang` and `unlit.slang` combine project material implementations with the supported Cluster entries. A material stores one shader reference. To add another pipeline, compose its entries into the same project shader definition; there is no shader substitution registry.

`standard_material.slang` contains the Standard material implementation without including the Cluster pipeline. Its small camera accessor is provided by the project entry file. The existing lighting, BRDF, shadow, geometry and vertex-decoding algorithms remain in their original form. `Game.Build` produces `assets/Shaders/generated/render_instance_layout.slang` from the registered rendering feature layout before compilation.

`Game.App` copies this project's built `.asset` files and manifest. It refuses to start when its own output manifest is absent. It does not use the engine repository's default content or search for a cache to compensate for an unbuilt project.

`EnginePath.props` and the local package source in `NuGet.config` record relative paths where possible. Move the project and engine together, or edit `EnginePath.props` and pass the new engine location to `Game.Build`. The engine checkout supplies its C# projects and native shader toolchain. The content build does not require any existing `.asset` files or `Library` directory from that checkout.

To reproduce an uncached content build, remove only this project's generated `.asset` outputs, `Library`, and `assets/Shaders/generated`, retaining the source files and source `.meta` settings, then run `Game.Build` again. Installation intentionally refuses a nonempty destination; it is not an update command.
