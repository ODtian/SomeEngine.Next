# focused-native-r35 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-mutual | Query | True | 4214 | 4300 | 0.97999999999999998 | 0.98445675592490001 | True | True |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1.5 | False | False |
| finite-small-mask8-oneway | Query | True | 3880 | 3865.5 | 1.0037511318070107 | 0.9760522905279766 | False | True |
| finite-small-mask8-oneway | Consume | False | 59 | 58 | 1.0172413793103448 | 1 | False | False |
| finite-middle-mask8-oneway | Query | True | 34427.5 | 33965.5 | 1.0136020373614403 | 1.0132711547548123 | False | False |
| finite-middle-mask8-oneway | Consume | False | 470 | 469.5 | 1.0010649627263046 | 0.99957770270270274 | False | True |
| finite-large-mask8-oneway | Query | True | 262492 | 261154.5 | 1.0051214893865508 | 1.0071951408021678 | False | True |
| finite-large-mask8-oneway | Consume | False | 4348 | 4058 | 1.0714637752587481 | 1.013579602108162 | False | False |
| finite-large-mask8-mutual | Query | True | 267463 | 268105 | 0.9976054157885903 | 1.0033269581906146 | False | True |
| finite-large-mask8-mutual | Consume | False | 4960 | 4938 | 1.0044552450384772 | 1.0508460653852472 | False | False |

Required metrics: 1/5 pass <=1.00; 4/5 pass <=1.01. Case reports: 5.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
