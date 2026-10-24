# optimized-7 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4145 | 4209 | 0.98479448800190073 | 1.0000951706197723 | False |
| finite-small-mask1-oneway | Consume | False | 9 | 10 | 0.90000000000000002 | 0.91666666666666663 | True |
| finite-small-mask4-mutual | Query | True | 4645.5 | 4654.5 | 0.9980663873670641 | 0.97540854004483013 | True |
| finite-small-mask4-mutual | Consume | False | 16 | 17 | 0.94117647058823528 | 0.94999999999999996 | True |
| default-small-dynamic10 | Query | True | 1276 | 1128.5 | 1.1307044749667701 | 1.1548582230623821 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1.0344827586206897 | False |
| character-small-hair16-clustered | Query | True | 4446 | 4408.5 | 1.008506294658047 | 1.0078311373267437 | False |
| character-small-hair16-clustered | Consume | False | 117 | 117 | 1 | 0.96720693554466619 | True |
| updates-small-sparse | MaintenanceAndQuery | True | 793 | 836.5 | 0.94799760908547515 | 0.91382765531062105 | True |
| updates-small-sparse | FrameTotal | True | 2088.5 | 2145.5 | 0.97343276625495223 | 0.94642571565648492 | True |
| updates-small-dense | MaintenanceAndQuery | True | 10533 | 15924.5 | 0.66143363998869664 | 0.68175105365399258 | True |
| updates-small-dense | FrameTotal | True | 13871.5 | 19155.5 | 0.72415233222834174 | 0.71579713559832125 | True |
| character-middle-hair64-interleaved | Query | True | 60350 | 61194 | 0.98620779815014548 | 0.97817505436387142 | True |
| character-middle-hair64-interleaved | Consume | False | 2018 | 2026 | 0.99605133267522217 | 0.96160799528733065 | True |
| updates-middle-sparse | MaintenanceAndQuery | True | 2604 | 2803.5 | 0.92883895131086147 | 0.91908155420190418 | True |
| updates-middle-sparse | FrameTotal | True | 7508 | 7777.5 | 0.96534876245580203 | 0.98330232945553486 | True |
| finite-large-mask8-mutual | Query | True | 282575 | 283623.5 | 0.99630319772515319 | 1.0002630031323116 | False |
| finite-large-mask8-mutual | Consume | False | 4482 | 4458.5 | 1.005270830996972 | 1.0096245160347721 | False |
| default-large-dynamic50 | Query | True | 303364 | 279645 | 1.084818251711992 | 1.113312996476763 | False |
| default-large-dynamic50 | Consume | False | 33164.5 | 33382.5 | 0.99346963229236873 | 0.99927839176566358 | True |
| character-large-hair16-interleaved | Query | True | 1200698 | 1207534 | 0.99433887575836377 | 1.0151015761620259 | False |
| character-large-hair16-interleaved | Consume | False | 43893.5 | 43809.5 | 1.0019173923464089 | 1.0023529906090465 | False |
| updates-large-sparse | MaintenanceAndQuery | True | 10324.5 | 11151 | 0.92588108689803605 | 0.9221554987415026 | True |
| updates-large-sparse | FrameTotal | True | 29023.5 | 30117 | 0.96369160274927779 | 0.97084560038567314 | True |

Required metrics: 10/16 pass. Case reports: 12.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
