# focused-native-r36 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-mutual | Query | True | 3933 | 3950 | 0.99569620253164559 | 1.0382403992189195 | False | False |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1 | True | True |
| finite-small-mask8-oneway | Query | True | 3868 | 4047 | 0.95576970595502841 | 0.98658636736928551 | True | True |
| finite-small-mask8-oneway | Consume | False | 58 | 59 | 0.98305084745762716 | 1 | True | True |
| finite-middle-mask8-oneway | Query | True | 32293 | 33365 | 0.96787052300314702 | 0.96774889215079274 | True | True |
| finite-middle-mask8-oneway | Consume | False | 470.5 | 471 | 0.99893842887473461 | 1.0249406175771971 | False | False |
| finite-large-mask8-oneway | Query | True | 274736.5 | 270386 | 1.0160899602790086 | 1.030979933037002 | False | False |
| finite-large-mask8-oneway | Consume | False | 4930.5 | 4886 | 1.0091076545231272 | 1.0196553581044696 | False | False |
| finite-large-mask8-mutual | Query | True | 283234.5 | 282697.5 | 1.0018995569469131 | 1.004912354009327 | False | True |
| finite-large-mask8-mutual | Consume | False | 5359 | 5410.5 | 0.99048147121338137 | 0.93230333377256558 | True | True |

Required metrics: 2/5 pass <=1.00; 3/5 pass <=1.01. Case reports: 5.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
