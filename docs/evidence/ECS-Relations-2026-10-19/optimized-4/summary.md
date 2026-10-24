# optimized-4 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4316.5 | 4373.5 | 0.98696696010060592 | 0.87321663843402964 | True |
| finite-small-mask1-oneway | Consume | False | 9 | 10 | 0.90000000000000002 | 0.92307692307692313 | True |
| finite-small-mask4-mutual | Query | True | 4720.5 | 4631 | 1.0193262794212914 | 1.0598131023831401 | False |
| finite-small-mask4-mutual | Consume | False | 16 | 16 | 1 | 1.0723860589812346 | False |
| default-small-dynamic10 | Query | True | 1697 | 1698.5 | 0.99911686782455111 | 1.0407402837754471 | False |
| default-small-dynamic10 | Consume | False | 29 | 28 | 1.0357142857142858 | 1.0666666666666667 | False |
| default-small-dynamic50 | Query | True | 1860.5 | 1792 | 1.0382254464285714 | 1.0358047352401312 | False |
| default-small-dynamic50 | Consume | False | 107 | 106 | 1.0094339622641511 | 1.0030004286326619 | False |
| default-small-dynamic90 | Query | True | 1930.5 | 1782 | 1.0833333333333333 | 1.0824323674979577 | False |
| default-small-dynamic90 | Consume | False | 141 | 140 | 1.0071428571428571 | 0.70969976905312437 | False |
| finite-large-mask8-mutual | Query | True | 283006.5 | 276723 | 1.0227068223458116 | 1.0422128142272895 | False |
| finite-large-mask8-mutual | Consume | False | 4640 | 4460.5 | 1.0402421253222733 | 1.0416350184263088 | False |
| default-large-dynamic50 | Query | True | 426058 | 418358 | 1.0184052892498769 | 1.0273744296086014 | False |
| default-large-dynamic50 | Consume | False | 33882.5 | 33585 | 1.0088581211850529 | 1.0172840633739766 | False |

Required metrics: 1/7 pass. Case reports: 7.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
