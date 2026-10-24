# focused-native-r42 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| threads-small-sparse-parallel | ScheduledPairOutput | True | 703 | 681 | 1.0323054331864905 | 0.89652942444492345 | False | False |
| threads-small-sparse-parallel | Consume | False | 454 | 456 | 0.99561403508771928 | 1.0038659793814433 | False | True |
| threads-small-uniform-parallel | ScheduledPairOutput | True | 2075.5 | 2251.5 | 0.92182989118365533 | 0.74701302188213181 | True | True |
| threads-small-uniform-parallel | Consume | False | 661 | 657.5 | 1.0053231939163498 | 1.022938443670151 | False | False |

Required metrics: 1/2 pass <=1.00; 1/2 pass <=1.01. Case reports: 2.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
