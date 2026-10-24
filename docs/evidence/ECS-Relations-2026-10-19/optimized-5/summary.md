# optimized-5 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4440.5 | 4286.5 | 1.0359267467630935 | 1.1010916781571063 | False |
| finite-small-mask1-oneway | Consume | False | 10 | 9 | 1.1111111111111112 | 1.1666666666666667 | False |
| finite-small-mask4-mutual | Query | True | 4638 | 5050 | 0.91841584158415845 | 0.94695809429864408 | True |
| finite-small-mask4-mutual | Consume | False | 16 | 17 | 0.94117647058823528 | 1.05 | False |
| default-small-dynamic10 | Query | True | 1591 | 1225 | 1.2987755102040817 | 1.1152631225216771 | False |
| default-small-dynamic10 | Consume | False | 29 | 29 | 1 | 0.9692671394799065 | True |
| default-small-dynamic50 | Query | True | 1610 | 1357.5 | 1.1860036832412524 | 1.2794104155836212 | False |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 1.0325892857142855 | False |
| default-small-dynamic90 | Query | True | 1665 | 1448.5 | 1.1494649637556094 | 1.1473451201861682 | False |
| default-small-dynamic90 | Consume | False | 140 | 140 | 1 | 0.97402597402597402 | True |
| finite-large-mask8-mutual | Query | True | 278637.5 | 295521 | 0.94286869630246239 | 0.96058681098356646 | True |
| finite-large-mask8-mutual | Consume | False | 4503 | 4500 | 1.0006666666666666 | 0.997656496462211 | False |
| default-large-dynamic50 | Query | True | 375437 | 307640.5 | 1.2203757307636673 | 1.2200732300871506 | False |
| default-large-dynamic50 | Consume | False | 33462 | 33314.5 | 1.0044275015383692 | 0.99740966661552355 | False |

Required metrics: 2/7 pass. Case reports: 7.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
