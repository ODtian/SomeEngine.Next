# machine-r25 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| default-small-dynamic50-normal | Query | True | 1251 | 1228 | 1.018729641693811 | 1.0527194324662681 | False |
| default-small-dynamic50-normal | Consume | False | 106 | 106 | 1 | 0.9821428571428571 | True |
| default-small-dynamic50-fixed | Query | True | 1252 | 1226 | 1.0212071778140293 | 0.99780131920847492 | False |
| default-small-dynamic50-fixed | Consume | False | 105 | 106 | 0.99056603773584906 | 0.9821428571428571 | True |
| finite-large-mask1-oneway-fixed | Query | True | 261708 | 271374.5 | 0.96437948296542231 | 0.96049355532295722 | True |
| finite-large-mask1-oneway-fixed | Consume | False | 607 | 612 | 0.99183006535947715 | 1.0367956819378614 | False |
| finite-large-mask1-oneway-normal | Query | True | 272395 | 260984 | 1.0437229868497686 | 1.0440907032186741 | False |
| finite-large-mask1-oneway-normal | Consume | False | 614.5 | 601.5 | 1.0216126350789692 | 0.98217078511104172 | False |

Required metrics: 1/4 pass. Case reports: 4.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
