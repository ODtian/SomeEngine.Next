# controlled-reads-r30 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| default-small-dynamic10 | Query | True | 1093 | 1090 | 1.0027522935779816 | 0.97959734259602738 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1.032258064516129 | False |
| finite-large-mask1-mutual | Query | True | 282810.5 | 285827.5 | 0.98944468254454176 | 0.97261966115116738 | True |
| finite-large-mask1-mutual | Consume | False | 35 | 34 | 1.0294117647058822 | 1.0332103321033217 | False |
| finite-large-mask4-oneway | Query | True | 300072.5 | 302205.5 | 0.9929418888802487 | 0.99604099936135737 | True |
| finite-large-mask4-oneway | Consume | False | 2649.5 | 2734 | 0.96909290416971472 | 1.0322951880889011 | False |
| default-large-dynamic50 | Query | True | 325448.5 | 325304 | 1.0004441998868749 | 0.93563420265186903 | False |
| default-large-dynamic50 | Consume | False | 38545.5 | 38627.5 | 0.99787716005436544 | 0.96125136609294926 | True |

Required metrics: 2/4 pass. Case reports: 4.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
