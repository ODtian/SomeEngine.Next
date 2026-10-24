# focused-native-r41 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-mutual | Query | True | 3898.5 | 3989.5 | 0.97719012407569872 | 0.96570134282478559 | True | True |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1.0625 | False | False |
| threads-small-sparse-parallel | ScheduledPairOutput | True | 716.5 | 708 | 1.0120056497175141 | 1.0508397639582387 | False | False |
| threads-small-sparse-parallel | Consume | False | 459 | 454 | 1.0110132158590308 | 0.97593800978792822 | False | False |
| threads-small-uniform-parallel | ScheduledPairOutput | True | 1990.5 | 2221.5 | 0.89601620526671166 | 0.79805706417807698 | True | True |
| threads-small-uniform-parallel | Consume | False | 636 | 644.5 | 0.98681148176881306 | 0.91768292682926833 | True | True |
| finite-large-mask8-mutual | Query | True | 268195.5 | 269146.5 | 0.99646660833412282 | 0.99303927719482099 | True | True |
| finite-large-mask8-mutual | Consume | False | 4888 | 4893 | 0.99897813202534236 | 1.0094829052559959 | False | True |
| default-large-dynamic50 | Query | True | 254856.5 | 275595.5 | 0.92474840844643691 | 0.92744820876828138 | True | True |
| default-large-dynamic50 | Consume | False | 34229.5 | 34390 | 0.99533294562372787 | 0.99019991899662041 | True | True |
| default-large-dynamic90 | Query | True | 266999.5 | 288378.5 | 0.92586479227820384 | 0.92870825463150841 | True | True |
| default-large-dynamic90 | Consume | False | 44955 | 44745 | 1.0046932618169628 | 0.99255318045572871 | False | True |

Required metrics: 5/6 pass <=1.00; 5/6 pass <=1.01. Case reports: 6.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
