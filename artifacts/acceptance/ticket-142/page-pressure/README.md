# Page capacity and raster work (#142 first slice)

The global page-pressure calculation now responds to reusable page capacity and allocation failures. Its target follows the submitted resolution bias plus log2(allocation / configured pool load), with the existing fast reduction and ten-frame delayed recovery.

Per-VSM work pressure uses the separate native throttle path. Native VirtualShadowMapArray.cpp:2823–2825 enables that path through a positive time/load budget; the default load budget is zero. VirtualShadowMapCacheManager.cpp:675–721 provides the independent global page-pressure response.

One public D3D12 shader case supplies ten reusable pages out of ten and growing material raster work. The previous mixed calculation raises both biases to .5; the page-only calculation preserves both at zero.

| Same case | Executed | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: |
| Mixed pressure | 1 | 0 | 1 | 0 |
| Page pressure | 1 | 1 | 0 | 0 |

The necessary Release build completed with zero warnings/errors. Both invocations ran the repository behavior rules with zero violations. The direct results are retained as red.trx and green.trx.

Command: `dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release --no-build --filter FullyQualifiedName~AvailablePageCapacityPreservesResolutionWhenRasterWorkGrows`.
