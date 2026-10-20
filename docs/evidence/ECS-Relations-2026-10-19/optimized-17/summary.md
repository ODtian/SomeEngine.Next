# optimized-17 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| default-small-dynamic10 | Query | True | 1132 | 1138.5 | 0.99429073342116825 | 0.88786111669682954 | True |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 0.91883614088820886 | True |
| default-small-dynamic50 | Query | True | 1332 | 1310 | 1.0167938931297711 | 0.9931525444179663 | False |
| default-small-dynamic50 | Consume | False | 111 | 109 | 1.0183486238532109 | 1 | False |
| default-small-dynamic90 | Query | True | 1379.5 | 1354 | 1.0188330871491875 | 1.1062188901861978 | False |
| default-small-dynamic90 | Consume | False | 144 | 144 | 1 | 1.2713375796178339 | False |
| character-small-hair16-clustered | Query | True | 4581 | 4483 | 1.0218603613651573 | 1.0322046152919202 | False |
| character-small-hair16-clustered | Consume | False | 120.5 | 120 | 1.0041666666666667 | 0.98777324935161182 | False |
| default-middle-dynamic10 | Query | True | 16207 | 16100 | 1.0066459627329192 | 0.99095919933202448 | False |
| default-middle-dynamic10 | Consume | False | 464 | 467 | 0.99357601713062094 | 0.99983387324528594 | True |
| default-middle-dynamic50 | Query | True | 18086.5 | 17908 | 1.0099676122403396 | 1.0010336733083147 | False |
| default-middle-dynamic50 | Consume | False | 1851 | 1799 | 1.0289049471928848 | 0.97481722177091834 | False |
| default-middle-dynamic90 | Query | True | 18867 | 18796.5 | 1.0037506982682947 | 1.0012706035133121 | False |
| default-middle-dynamic90 | Consume | False | 2443 | 2438.5 | 1.0018453967603034 | 1.0057213491082464 | False |
| character-middle-hair64-interleaved | Query | True | 63064.5 | 60973 | 1.034302068128516 | 1.0238885379774822 | False |
| character-middle-hair64-interleaved | Consume | False | 2308.5 | 2320.5 | 0.99482870071105367 | 0.989443460619294 | True |
| default-large-dynamic10 | Query | True | 247665 | 248143 | 0.99807369137956747 | 0.99244240602079703 | True |
| default-large-dynamic10 | Consume | False | 8185.5 | 8301 | 0.98608601373328519 | 1.0166173153601934 | False |
| default-large-dynamic50 | Query | True | 277145 | 271486.5 | 1.0208426570013611 | 1.0201506383871994 | False |
| default-large-dynamic50 | Consume | False | 33972 | 33355 | 1.018497976315395 | 1.0222249645215029 | False |
| default-large-dynamic90 | Query | True | 283055.5 | 282306 | 1.0026549205472077 | 0.99728116369477815 | False |
| default-large-dynamic90 | Consume | False | 44368 | 44214.5 | 1.0034717117687637 | 0.98661706678544869 | False |
| character-large-hair64-clustered | Query | True | 985835.5 | 1028656 | 0.95837238104866929 | 0.96186877128710302 | True |
| character-large-hair64-clustered | Consume | False | 43527 | 43235.5 | 1.0067421447652971 | 1.0009239370184713 | False |

Required metrics: 3/12 pass. Case reports: 12.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
