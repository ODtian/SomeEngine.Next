# focused-native-r45 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| threads-small-sparse-parallel | ScheduledPairOutput | True | 555 | 742.5 | 0.74747474747474751 | 0.72102040816326529 | True | True |
| threads-small-sparse-parallel | Consume | False | 457 | 467.5 | 0.97754010695187166 | 0.99505562422744132 | True | True |
| threads-small-uniform-parallel | ScheduledPairOutput | True | 1735 | 2176 | 0.79733455882352944 | 0.79762624735409737 | True | True |
| threads-small-uniform-parallel | Consume | False | 623.5 | 631 | 0.98811410459587956 | 1.0411262258778868 | False | False |

Required metrics: 2/2 pass <=1.00; 2/2 pass <=1.01. Case reports: 2.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
