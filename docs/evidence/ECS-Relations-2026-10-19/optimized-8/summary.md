# optimized-8 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4223.5 | 4453.5 | 0.94835522622656343 | 0.61298335165210482 | True |
| finite-small-mask1-oneway | Consume | False | 9 | 10 | 0.90000000000000002 | 0.70588235294117652 | True |
| finite-small-mask4-mutual | Query | True | 4549 | 4592.5 | 0.9905280348394121 | 1.029678602451501 | False |
| finite-small-mask4-mutual | Consume | False | 16 | 16 | 1 | 1 | True |
| default-small-dynamic10 | Query | True | 1141 | 1114.5 | 1.0237774786899956 | 1.0455006956717929 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1.0689655172413792 | False |
| default-small-dynamic50 | Query | True | 1262 | 1246 | 1.0128410914927768 | 0.97281207220400534 | False |
| default-small-dynamic50 | Consume | False | 106 | 106 | 1 | 0.96334185848252385 | True |
| default-small-dynamic90 | Query | True | 1304 | 1271.5 | 1.0255603617774283 | 1.0141257317383554 | False |
| default-small-dynamic90 | Consume | False | 140 | 139 | 1.0071942446043165 | 1.0180272108843536 | False |
| character-small-hair16-clustered | Query | True | 4888.5 | 4483 | 1.0904528217711353 | 1.095232464179428 | False |
| character-small-hair16-clustered | Consume | False | 118 | 116 | 1.0172413793103448 | 0.99494753206373898 | False |
| updates-small-sparse | MaintenanceAndQuery | True | 802 | 844.5 | 0.94967436352871526 | 0.95374215354901026 | True |
| updates-small-sparse | FrameTotal | True | 2090 | 2146 | 0.97390493942218082 | 0.94387060613339768 | True |
| bitmap-space-small-dense-oneway | Query | True | 4245.5 | 7192.5 | 0.59026763990267639 | 0.61017270709357552 | True |
| bitmap-space-small-dense-oneway | Consume | False | 2663 | 2633.5 | 1.0112018226694512 | 1.0181591395062528 | False |
| complete-small-dense | Query | True | 9723.5 | 14921 | 0.65166543797332621 | 0.65257727542546606 | True |
| complete-small-dense | FrameTotal | True | 11702 | 16929.5 | 0.69121946897427566 | 0.68927433723880993 | True |
| complete-small-dense | Consume | False | 1958 | 1961 | 0.99847016828148905 | 0.97886797431612271 | True |
| character-middle-hair64-interleaved | Query | True | 59682.5 | 60427.5 | 0.9876711762028878 | 0.97436706887413094 | True |
| character-middle-hair64-interleaved | Consume | False | 2030 | 2030 | 1 | 1.0095072751322751 | False |
| finite-large-mask8-mutual | Query | True | 276340.5 | 276590 | 0.99909794280342745 | 0.99608618636951518 | True |
| finite-large-mask8-mutual | Consume | False | 4320.5 | 4300.5 | 1.0046506220206952 | 0.99226573079784086 | False |
| default-large-dynamic10 | Query | True | 248632.5 | 248118.5 | 1.0020715907923028 | 1.0086361612000228 | False |
| default-large-dynamic10 | Consume | False | 8051.5 | 8125.5 | 0.99089286813119193 | 0.98932465049974883 | True |
| default-large-dynamic50 | Query | True | 280530 | 273366.5 | 1.026204747106906 | 1.0297300614007354 | False |
| default-large-dynamic50 | Consume | False | 33789 | 33835 | 0.99864046106103144 | 0.98867981037232122 | True |
| default-large-dynamic90 | Query | True | 290893.5 | 282235.5 | 1.0306765095106747 | 1.0215652818059264 | False |
| default-large-dynamic90 | Consume | False | 44136.5 | 44340 | 0.99541046459179072 | 0.99246631411261377 | True |
| character-large-hair16-interleaved | Query | True | 1189074 | 1202049.5 | 0.98920551940664669 | 0.98920056802498202 | True |
| character-large-hair16-interleaved | Consume | False | 44598.5 | 44566.5 | 1.0007180281152883 | 0.99815451255778787 | False |

Required metrics: 9/17 pass. Case reports: 15.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
