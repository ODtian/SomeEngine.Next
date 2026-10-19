# controlled-inline-r31 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| default-small-dynamic10 | Query | True | 1192 | 1061 | 1.1234684260131951 | 1.1492156001756415 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 0.96666666666666667 | True |
| default-large-dynamic50 | Query | True | 311122.5 | 273507.5 | 1.1375282213467639 | 1.1311168276851726 | False |
| default-large-dynamic50 | Consume | False | 34162 | 33918 | 1.0071938203903532 | 1.003893646425799 | False |

Required metrics: 0/2 pass. Case reports: 2.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
