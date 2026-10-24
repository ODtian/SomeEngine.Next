# focused-native-r38 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-mutual | Query | True | 4096 | 4122 | 0.99369238233867052 | 1.0121711603536732 | False | False |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1.25 | False | False |
| finite-small-mask8-oneway | Query | True | 4151 | 4129.5 | 1.0052064414578037 | 0.99017555161861814 | False | True |
| finite-small-mask8-oneway | Consume | False | 60 | 60 | 1 | 1.0149253731343284 | False | False |
| finite-middle-mask8-oneway | Query | True | 33351 | 33371.5 | 0.99938570336964172 | 1.0071332910410939 | False | True |
| finite-middle-mask8-oneway | Consume | False | 468 | 471 | 0.99363057324840764 | 0.97789602947196075 | True | True |
| finite-large-mask8-oneway | Query | True | 272837 | 271753.5 | 1.0039870691637827 | 1.0012326418146156 | False | True |
| finite-large-mask8-oneway | Consume | False | 4810.5 | 4770.5 | 1.0083848653181009 | 0.99607149024348129 | False | True |
| finite-large-mask8-mutual | Query | True | 269171.5 | 268462.5 | 1.0026409647529915 | 0.99899218058996198 | False | True |
| finite-large-mask8-mutual | Consume | False | 4749.5 | 4756 | 0.9986333052985702 | 0.96583153537933164 | True | True |

Required metrics: 0/5 pass <=1.00; 4/5 pass <=1.01. Case reports: 5.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
