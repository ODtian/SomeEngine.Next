# Asset files and domain objects

The current asset model is specified in [docs/AssetSystem.md](../../docs/AssetSystem.md). Asset IDs are GUID values; `Asset<T>` is independent access to disk fields, and Texture/Mesh/Shader/Material are ordinary domain objects created explicitly from those fields or from application data.

A Loader owns its reads and file cache. It never owns the application's objects or borrowed storage. Multiple opens and multiple instances may coexist, including different versions of the same asset. Concrete binary-document access keeps old byte ranges valid after an Asset or Loader closes. Updates validate and prepare new data before replacing live content; Texture switches through the existing frame execution boundary and delays old GPU release until actual submitted uses finish.

Game world and Render world remain the only world data route. Scene files instantiate the existing World; there is no identity wrapper or additional asset world. File metadata, dependencies and input/output import records belong to AssetEntry, AssetManifest and SourceMeta. Runtime Material stores concrete references and generated properties; its file stores IDs and overrides.

The earlier requirement that a decoded document root must be the same object as the runtime value no longer applies. Neither a unique runtime object per GUID nor a loader-wide retirement barrier is part of the asset model.
