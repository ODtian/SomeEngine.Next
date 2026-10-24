# focused-native-r43 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| threads-small-sparse-parallel | ScheduledPairOutput | True | 741 | 729 | 1.0164609053497942 | 1.0554293502953203 | False | False |
| threads-small-sparse-parallel | Consume | False | 461 | 467 | 0.98715203426124198 | 0.98548320199087514 | True | True |
| threads-small-uniform-parallel | ScheduledPairOutput | True | 2189 | 2352 | 0.93069727891156462 | 0.83882921927014631 | True | True |
| threads-small-uniform-parallel | Consume | False | 672.5 | 667 | 1.0082458770614693 | 1.0309334793320559 | False | False |

Required metrics: 1/2 pass <=1.00; 1/2 pass <=1.01. Case reports: 2.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
