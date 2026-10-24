# optimized-14 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4221 | 4642.5 | 0.90920840064620356 | 0.94650281360586308 | True |
| finite-small-mask1-oneway | Consume | False | 9 | 10 | 0.90000000000000002 | 1 | True |
| finite-small-mask1-mutual | Query | True | 4306.5 | 4080 | 1.0555147058823529 | 1.0427842456208058 | False |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1.6666666666666667 | False |
| finite-small-mask4-oneway | Query | True | 4585.5 | 4428.5 | 1.0354521847126568 | 1.1456956922254031 | False |
| finite-small-mask4-oneway | Consume | False | 32 | 32 | 1 | 1.0458333333333327 | False |
| finite-small-mask4-mutual | Query | True | 4704.5 | 4476 | 1.0510500446827524 | 1.0858534059565657 | False |
| finite-small-mask4-mutual | Consume | False | 16 | 16.5 | 0.96969696969696972 | 1.0178117048346067 | False |
| finite-small-mask8-oneway | Query | True | 4176.5 | 4088.5 | 1.0215237862296687 | 1.1291195087073636 | False |
| finite-small-mask8-oneway | Consume | False | 59 | 58 | 1.0172413793103448 | 1.4547619047619045 | False |
| finite-small-mask8-mutual | Query | True | 4336 | 4205 | 1.0311533888228299 | 0.94377556645101435 | False |
| finite-small-mask8-mutual | Consume | False | 58 | 58 | 1 | 0.97058823529411764 | True |
| default-small-dynamic10 | Query | True | 1141.5 | 1128 | 1.011968085106383 | 1.0700559382016059 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1.0344827586206897 | False |
| default-small-dynamic50 | Query | True | 1263.5 | 1253 | 1.0083798882681565 | 1.0153281802698806 | False |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 1.009090909090909 | False |
| default-small-dynamic90 | Query | True | 1316.5 | 1310 | 1.0049618320610687 | 1.0827790788446521 | False |
| default-small-dynamic90 | Consume | False | 140 | 140 | 1 | 0.98489822718319131 | True |
| character-small-hair16-clustered | Query | True | 4384 | 4287 | 1.0226265453697223 | 0.94227065433244517 | False |
| character-small-hair16-clustered | Consume | False | 116 | 116 | 1 | 0.85872576177285531 | True |
| character-middle-hair64-interleaved | Query | True | 60936.5 | 61154.5 | 0.99643525823937729 | 0.9909627398585259 | True |
| character-middle-hair64-interleaved | Consume | False | 2046 | 2088.5 | 0.97965046684223123 | 0.99021079815026114 | True |
| finite-large-mask4-mutual | Query | True | 296345 | 293678.5 | 1.0090796568356213 | 1.0141933825092542 | False |
| finite-large-mask4-mutual | Consume | False | 1139.5 | 1136.5 | 1.0026396832380113 | 1.0095715912325653 | False |
| default-large-dynamic10 | Query | True | 250947 | 251416 | 0.99813456581920001 | 0.99602300665945676 | True |
| default-large-dynamic10 | Consume | False | 7983.5 | 7924.5 | 1.007445264685469 | 1.0321947734329917 | False |
| default-large-dynamic50 | Query | True | 281884.5 | 280009 | 1.0066979989928895 | 0.99974779455924856 | False |
| default-large-dynamic50 | Consume | False | 33547.5 | 33446 | 1.0030347425701129 | 0.99852429173617208 | False |
| default-large-dynamic90 | Query | True | 291866 | 291451 | 1.001423910022611 | 0.99193260612391143 | False |
| default-large-dynamic90 | Consume | False | 44513 | 44938 | 0.9905425252570208 | 0.99789539260220628 | True |
| character-large-hair16-interleaved | Query | True | 1147647 | 1145479 | 1.0018926580059522 | 0.99257176224824628 | False |
| character-large-hair16-interleaved | Consume | False | 43622 | 43393.5 | 1.0052657656100568 | 0.9893024318575907 | False |

Required metrics: 3/16 pass. Case reports: 16.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
