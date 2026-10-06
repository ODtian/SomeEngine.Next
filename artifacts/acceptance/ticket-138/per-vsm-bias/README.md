# Directional per-view resolution bias — #138 first slice

Integration base: `2a55ca9006e586072d11ea01193662deee567d4f`.
Branch: `codex/rg70-ticket-138`.

The native reference is UE5.8.3 commit
`396c9f059903aed5fec78ecd3d437a40c6415368`:
`Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapProjectionCommon.ush:41–91`.
GitBlob `302b78ccc27d19931b193aa7e4aee0ddcc14ce60`, SHA256
`47628D6CDB7439236231C22634DF30B7D479D9F92BB2E7E719F2AEAB191A2DF0`.
The original file was read from the authenticated fixed reference and is retained
outside the repository while the native audit/implementation uses it.

At a receiver distance of 192 centimeters, the unbiased absolute level lies
between seven and eight. With first level six, the native selector reads the
bias of index one. Its bias of one then selects index two. The fixture writes
shadow depth only at index two and gives the unrelated index zero a bias of five.

The existing selector used index zero's bias. The repaired selector first finds
the unbiased distance view and then applies that view's bias. The public shader
sampler's actual visibility proves the result. The test supplies its own typed
view parameters, page data and depth pixels through public RHI compute/readback.

## Actual red and green

```powershell
dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release --no-build --filter FullyQualifiedName~NativeShadowProjectionTests.DirectionalSamplingUsesTheBiasOfTheUnbiasedDistanceLevel --results-directory <proof> --logger 'trx;LogFileName=per-vsm-bias.trx'
```

- Red: unchanged production2a55 with the new native-derived test;
  **1 executed / 1 failed / 0 skipped**, expected visibility0, actual1.
- Green: minimal selector source change with the same test;
  **1 executed / 1 passed / 0 skipped**, expected visibility0 retained.
- Both commands ran the default behavior-rule checker: **0 violations**.
- The prepared Release test project built with **0 warnings / 0 errors**.
- D3D12 WARP, .NET SDK10.0.112/runtime10.0.12, Slang2026.17.1.
  The fixture compiles its shader include from the owned checkout during the test.

Both runs used unchanged test source SHA256
`702565A313DF49717911C096C89099AB5D0ECB10AC6CAA995D04BF1450EFFB80`.
Green shader working-file SHA256
`BF70665B13BE9E13F5CC08977961D95F984FA34993A402B45FD136A322105981`.
The commit's Git blobs define checkout-independent source identity; these hashes
record the actual working bytes used by the runs.

`red.{log,trx}` and `green.{log,trx}` retain commands' actual output and result rows.
This is the per-unbiased-view-bias slice. Remaining #138 camera/unit calibration,
per-light/moving parameters, local nominal footprint/mask/mip mode and conditional
inputs continue in subsequent native-derived red/green slices. Parent #70 and
the final default-scene correctness/quality/performance gates remain active.
