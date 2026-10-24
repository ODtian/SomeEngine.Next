# optimized-10 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask4-mutual | Query | True | 4740 | 4885.5 | 0.97021799201719372 | 1.0068215968067553 | False |
| finite-small-mask4-mutual | Consume | False | 16 | 17 | 0.94117647058823528 | 0.95238095238095233 | True |
| default-small-dynamic10 | Query | True | 1163.5 | 1193.5 | 0.97486384583158781 | 0.84483710657095568 | True |
| default-small-dynamic10 | Consume | False | 29 | 29 | 1 | 0.72684642438452562 | True |
| default-small-dynamic50 | Query | True | 1251 | 1272.5 | 0.98310412573673867 | 1.0100728744939274 | False |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 0.97692307692307656 | False |
| default-small-dynamic90 | Query | True | 1300 | 1288 | 1.0093167701863355 | 1.0174727194804336 | False |
| default-small-dynamic90 | Consume | False | 140 | 139 | 1.0071942446043165 | 1.023000343288706 | False |
| character-small-hair16-clustered | Query | True | 4825 | 4514 | 1.068896765618077 | 1.0959623700478363 | False |
| character-small-hair16-clustered | Consume | False | 119 | 119 | 1 | 1.0612168719671506 | False |
| character-middle-hair64-interleaved | Query | True | 61549 | 60297.5 | 1.0207554210373564 | 1.0190958477033287 | False |
| character-middle-hair64-interleaved | Consume | False | 2036 | 2010 | 1.0129353233830847 | 1.0534440790878421 | False |
| default-large-dynamic10 | Query | True | 248276.5 | 252164 | 0.98458344569407208 | 0.98807216931058395 | True |
| default-large-dynamic10 | Consume | False | 7946.5 | 8111 | 0.97971890025890762 | 0.9936011299676849 | True |
| default-large-dynamic50 | Query | True | 276485 | 278825.5 | 0.99160586101342951 | 0.98432332155023694 | True |
| default-large-dynamic50 | Consume | False | 33039 | 33402.5 | 0.98911758101938474 | 0.97941676478031714 | True |
| default-large-dynamic90 | Query | True | 294710.5 | 293083.5 | 1.0055513189927103 | 1.0004755628278879 | False |
| default-large-dynamic90 | Consume | False | 45340.5 | 45060.5 | 1.0062138680218817 | 1.0249907008709755 | False |
| character-large-hair16-interleaved | Query | True | 1173721.5 | 1232006.5 | 0.9526909963543212 | 0.95215726262054834 | True |
| character-large-hair16-interleaved | Consume | False | 43646 | 43530 | 1.0026648288536641 | 1.0069503441579832 | False |

Required metrics: 4/10 pass. Case reports: 10.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
