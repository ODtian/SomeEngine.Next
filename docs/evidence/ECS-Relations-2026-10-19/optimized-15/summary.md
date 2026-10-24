# optimized-15 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4207.5 | 4252 | 0.98953433678269054 | 1.2327749556523133 | False |
| finite-small-mask1-oneway | Consume | False | 10 | 10 | 1 | 1.0862619808306726 | False |
| finite-small-mask1-mutual | Query | True | 4370 | 4383 | 0.99703399498060685 | 0.94802408404287186 | True |
| finite-small-mask1-mutual | Consume | False | 3 | 3 | 1 | 1 | True |
| finite-small-mask4-oneway | Query | True | 4422.5 | 4517.5 | 0.9789706696181516 | 0.94767167825777465 | True |
| finite-small-mask4-oneway | Consume | False | 31 | 31 | 1 | 0.91891891891891897 | True |
| finite-small-mask4-mutual | Query | True | 4432 | 4472 | 0.99105545617173529 | 0.93550897237590025 | True |
| finite-small-mask4-mutual | Consume | False | 16 | 17 | 0.94117647058823528 | 0.94999999999999996 | True |
| finite-small-mask8-oneway | Query | True | 4245 | 4155.5 | 1.0215377210925281 | 1.0218071489523088 | False |
| finite-small-mask8-oneway | Consume | False | 58 | 58 | 1 | 0.96969696969696972 | True |
| finite-small-mask8-mutual | Query | True | 4118 | 4227 | 0.97421339011119001 | 0.94983260471164022 | True |
| finite-small-mask8-mutual | Consume | False | 58 | 58 | 1 | 1 | True |
| default-small-dynamic10 | Query | True | 1137 | 1134 | 1.0026455026455026 | 0.91308807439824968 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 0.967741935483871 | True |
| default-small-dynamic50 | Query | True | 1270 | 1258 | 1.0095389507154213 | 0.96685914368204429 | False |
| default-small-dynamic50 | Consume | False | 106 | 106 | 1 | 0.97728246892413217 | True |
| default-small-dynamic90 | Query | True | 1315 | 1302.5 | 1.0095969289827256 | 1.0170284017312734 | False |
| default-small-dynamic90 | Consume | False | 140 | 140 | 1 | 0.99800399201596834 | True |
| character-small-hair16-clustered | Query | True | 4348.5 | 4631.5 | 0.93889668573896146 | 0.72199782401740809 | True |
| character-small-hair16-clustered | Consume | False | 118 | 119 | 0.99159663865546221 | 0.88421847470425374 | True |
| character-middle-hair64-interleaved | Query | True | 56457.5 | 60771.5 | 0.92901277737097154 | 0.92179233462713184 | True |
| character-middle-hair64-interleaved | Consume | False | 2008 | 2032 | 0.98818897637795278 | 0.97848847888646218 | True |
| finite-large-mask4-mutual | Query | True | 301858 | 305346 | 0.9885768930983212 | 0.97329500175662587 | True |
| finite-large-mask4-mutual | Consume | False | 1173 | 1183.5 | 0.99112801013941698 | 0.98543929738831848 | True |
| default-large-dynamic10 | Query | True | 253375.5 | 255267 | 0.99259011153028009 | 0.99457569877336127 | True |
| default-large-dynamic10 | Consume | False | 8261.5 | 8370 | 0.98703703703703705 | 0.96426381844621334 | True |
| default-large-dynamic50 | Query | True | 280687.5 | 280888.5 | 0.9992844135662371 | 1.0080257937505079 | False |
| default-large-dynamic50 | Consume | False | 33165 | 33433 | 0.99198396793587174 | 0.99924681328337539 | True |
| default-large-dynamic90 | Query | True | 292030.5 | 292650.5 | 0.99788143194698109 | 0.99408528247441907 | True |
| default-large-dynamic90 | Consume | False | 44404 | 44598.5 | 0.99563886677803071 | 0.99069576641274493 | True |
| character-large-hair16-interleaved | Query | True | 1158961.5 | 1158661.5 | 1.0002589194514533 | 1.0174027101224457 | False |
| character-large-hair16-interleaved | Consume | False | 43694.5 | 44085 | 0.99114211182942047 | 1.0108395581686811 | False |

Required metrics: 9/16 pass. Case reports: 16.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
