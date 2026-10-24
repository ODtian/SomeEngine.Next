# optimized-6 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4134 | 4404.5 | 0.93858553751844709 | 0.91770084911822325 | True |
| finite-small-mask1-oneway | Consume | False | 9 | 10 | 0.90000000000000002 | 0.91666666666666663 | True |
| default-small-dynamic10 | Query | True | 1365 | 1137 | 1.2005277044854881 | 1.1775937995420114 | False |
| default-small-dynamic10 | Consume | False | 29 | 28 | 1.0357142857142858 | 1 | False |
| default-small-dynamic50 | Query | True | 1397.5 | 1243 | 1.1242960579243766 | 0.84391521197007469 | False |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 0.80028581636298979 | False |
| default-small-dynamic90 | Query | True | 1431 | 1292.5 | 1.10715667311412 | 1.0430377422020873 | False |
| default-small-dynamic90 | Consume | False | 139 | 140 | 0.99285714285714288 | 0.94044585987261131 | True |
| character-small-hair16-clustered | Query | True | 4449.5 | 4463 | 0.99697512883710504 | 0.95473100473678152 | True |
| character-small-hair16-clustered | Consume | False | 117 | 116 | 1.0086206896551724 | 0.97206276310753936 | False |
| bitmap-space-small-sparse-oneway | Query | True | 402 | 421 | 0.95486935866983369 | 0.96475652407855805 | True |
| bitmap-space-small-sparse-oneway | Consume | False | 243 | 245 | 0.99183673469387756 | 1.0082953131480714 | False |
| complete-small-sparse | Query | True | 181.5 | 238 | 0.76260504201680668 | 0.62217860647693779 | True |
| complete-small-sparse | FrameTotal | True | 262 | 316 | 0.82911392405063289 | 0.70872047464457599 | True |
| complete-small-sparse | Consume | False | 76.5 | 76.5 | 1 | 1.0108695652173914 | False |
| updates-small-sparse | MaintenanceAndQuery | True | 943 | 902 | 1.0454545454545454 | 1.032343441593397 | False |
| updates-small-sparse | FrameTotal | True | 2294.5 | 2257 | 1.0166149756313692 | 1.0624386066544087 | False |
| consume-small-sparse | PairsAndConsume | True | 265.5 | 320 | 0.82968750000000002 | 0.87593052109181135 | True |
| consume-small-sparse | Consume | False | 78 | 78 | 1 | 1.063391442155309 | False |
| threads-small-sparse-inline | ScheduledPairOutput | True | 249.5 | 300.5 | 0.83028286189683864 | 0.85778340968905364 | True |
| threads-small-sparse-inline | Consume | False | 107 | 105 | 1.019047619047619 | 0.97234330377273326 | False |
| threads-small-sparse-parallel | ScheduledPairOutput | True | 1205.5 | 1256.5 | 0.95941106247512931 | 0.91580874628568021 | True |
| threads-small-sparse-parallel | Consume | False | 526 | 530.5 | 0.99151743638077283 | 0.9817041296393102 | True |
| character-space-small-clustered | Query | True | 16858.5 | 18176 | 0.92751430457746475 | 0.94211564101123302 | True |
| character-space-small-clustered | Consume | False | 16633 | 16218.5 | 1.0255572340228751 | 1.0247072764720819 | False |
| character-middle-hair64-interleaved | Query | True | 61715 | 60337 | 1.0228383910370089 | 1.0115675443023864 | False |
| character-middle-hair64-interleaved | Consume | False | 2053 | 2008.5 | 1.0221558376898183 | 1.0297202054939922 | False |
| finite-large-mask8-mutual | Query | True | 277973 | 286230.5 | 0.97115087315991833 | 0.96090625033511701 | True |
| finite-large-mask8-mutual | Consume | False | 4412 | 4372 | 1.0091491308325709 | 0.99952111776746999 | False |
| default-large-dynamic10 | Query | True | 309066 | 252581.5 | 1.2236288089191014 | 1.23110446020175 | False |
| default-large-dynamic10 | Consume | False | 8244 | 7977 | 1.0334712297856337 | 1.0050868646807778 | False |
| default-large-dynamic50 | Query | True | 318904.5 | 279010.5 | 1.1429838661985838 | 1.1577415375859377 | False |
| default-large-dynamic50 | Consume | False | 33253.5 | 33219 | 1.0010385622685813 | 1.0062997071066921 | False |
| default-large-dynamic90 | Query | True | 323854 | 290278 | 1.1156684281964186 | 1.121717894093373 | False |
| default-large-dynamic90 | Consume | False | 44328.5 | 44689.5 | 0.99192203985276184 | 0.99201918424872204 | True |

Required metrics: 10/19 pass. Case reports: 17.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
