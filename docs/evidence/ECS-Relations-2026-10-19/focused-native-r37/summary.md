# focused-native-r37 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-mutual | Query | True | 3951 | 4191 | 0.94273443092340725 | 0.97982846251588307 | True | True |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1 | True | True |
| finite-small-mask8-oneway | Query | True | 4024 | 4099.5 | 0.98158311989266989 | 0.95478695143980452 | True | True |
| finite-small-mask8-oneway | Consume | False | 60 | 60 | 1 | 0.98550724637681164 | True | True |
| finite-middle-mask8-oneway | Query | True | 32072.5 | 31986 | 1.002704308134809 | 1.0019556619331864 | False | True |
| finite-middle-mask8-oneway | Consume | False | 478 | 480 | 0.99583333333333335 | 1.0045155993431856 | False | True |
| finite-large-mask8-oneway | Query | True | 272924 | 282110.5 | 0.96743651866910307 | 0.97027681725196335 | True | True |
| finite-large-mask8-oneway | Consume | False | 4780 | 4795.5 | 0.99676780314878533 | 0.97578609625668444 | True | True |
| finite-large-mask8-mutual | Query | True | 284096 | 267628 | 1.0615331729116535 | 1.0545340076956085 | False | False |
| finite-large-mask8-mutual | Consume | False | 4788 | 4741 | 1.0099135203543557 | 1.0068177793518682 | False | True |

Required metrics: 3/5 pass <=1.00; 4/5 pass <=1.01. Case reports: 5.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
