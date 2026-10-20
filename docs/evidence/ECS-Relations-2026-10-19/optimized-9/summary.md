# optimized-9 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask4-mutual | Query | True | 4580.5 | 4612.5 | 0.99306233062330618 | 1.0368907734254471 | False |
| finite-small-mask4-mutual | Consume | False | 17 | 17 | 1 | 1.05 | False |
| default-small-dynamic10 | Query | True | 1112.5 | 1125 | 0.98888888888888893 | 0.99879023388811505 | True |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1 | True |
| default-small-dynamic50 | Query | True | 1245 | 1254 | 0.99282296650717705 | 1.0005999999999999 | False |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 1.0268696820420959 | False |
| default-small-dynamic90 | Query | True | 1360 | 1344.5 | 1.0115284492376349 | 0.98027577390192666 | False |
| default-small-dynamic90 | Consume | False | 141 | 140 | 1.0071428571428571 | 0.99576960624796629 | False |
| character-small-hair16-clustered | Query | True | 4519 | 4447.5 | 1.0160764474423833 | 1.0068531537289218 | False |
| character-small-hair16-clustered | Consume | False | 119 | 118 | 1.0084745762711864 | 1.009923664122137 | False |
| character-middle-hair64-interleaved | Query | True | 62282.5 | 60428 | 1.0306894155027471 | 1.035567250470385 | False |
| character-middle-hair64-interleaved | Consume | False | 2036 | 2048.5 | 0.99389797412741032 | 0.976230669227933 | True |
| default-large-dynamic10 | Query | True | 248051 | 250935.5 | 0.98850501423672621 | 0.9785265431165836 | True |
| default-large-dynamic10 | Consume | False | 8017 | 7991.5 | 1.0031908903209661 | 1.0037536968226279 | False |
| default-large-dynamic50 | Query | True | 277224 | 278723 | 0.99462190059664968 | 0.99000096264238946 | True |
| default-large-dynamic50 | Consume | False | 33012.5 | 32968 | 1.0013497937393836 | 0.98909002018103209 | False |
| default-large-dynamic90 | Query | True | 291756 | 289472 | 1.0078902277249613 | 1.0012040575538175 | False |
| default-large-dynamic90 | Consume | False | 44442 | 44419.5 | 1.0005065342923716 | 1.0052898454642274 | False |
| character-large-hair16-interleaved | Query | True | 1175708.5 | 1252197.5 | 0.9389161853461615 | 0.94155229260128215 | True |
| character-large-hair16-interleaved | Consume | False | 43317 | 43464 | 0.99661789066813911 | 0.98503327300481813 | True |

Required metrics: 4/10 pass. Case reports: 10.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
