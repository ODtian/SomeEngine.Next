# Platform Surface

Backend-neutral product code does not acquire Windows UI/framework dependencies merely because one graphics backend targets Direct3D 12. Native platform integration remains behind the relevant backend or application boundary.

Graphics backends may expose typed native interop when a real external integration requires it; portable identities do not become backend-private types.

参见 [[architecture/RHI/Core/Portable-execution-boundary]].
