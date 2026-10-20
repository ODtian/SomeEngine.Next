# focused-native-r44 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| threads-small-sparse-parallel | ScheduledPairOutput | True | 715 | 705.5 | 1.0134656272147413 | 0.94407364787111625 | False | False |
| threads-small-sparse-parallel | Consume | False | 464.5 | 463.5 | 1.0021574973031284 | 0.97519685039370074 | False | True |
| threads-small-uniform-parallel | ScheduledPairOutput | True | 1894.5 | 2195.5 | 0.86290138920519244 | 0.73373909217569844 | True | True |
| threads-small-uniform-parallel | Consume | False | 632 | 638 | 0.99059561128526641 | 1.0274372446001168 | False | False |

Required metrics: 1/2 pass <=1.00; 1/2 pass <=1.01. Case reports: 2.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
