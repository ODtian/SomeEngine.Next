# optimized-16 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4431 | 4283.5 | 1.0344344578031983 | 1.1395755763087754 | False |
| finite-small-mask1-oneway | Consume | False | 11 | 10 | 1.1000000000000001 | 1.203846153846152 | False |
| finite-small-mask1-mutual | Query | True | 4517 | 4218 | 1.0708866761498341 | 1.0385017223365849 | False |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1 | True |
| finite-small-mask4-oneway | Query | True | 4524.5 | 4362 | 1.0372535534158642 | 0.98945466100011925 | False |
| finite-small-mask4-oneway | Consume | False | 31 | 30 | 1.0333333333333334 | 0.94444444444444442 | False |
| finite-small-mask4-mutual | Query | True | 4887.5 | 4404 | 1.1097865576748411 | 1.117793459139613 | False |
| finite-small-mask4-mutual | Consume | False | 16 | 16 | 1 | 1 | True |
| finite-small-mask8-oneway | Query | True | 4205.5 | 4188 | 1.0041786055396371 | 0.96307618471297751 | False |
| finite-small-mask8-oneway | Consume | False | 59 | 58 | 1.0172413793103448 | 0.98484848484848486 | False |
| finite-small-mask8-mutual | Query | True | 4215 | 4210.5 | 1.0010687566797292 | 0.91896957371296417 | False |
| finite-small-mask8-mutual | Consume | False | 58.5 | 58 | 1.0086206896551724 | 0.97524381095273849 | False |
| default-small-dynamic10 | Query | True | 1117 | 1110 | 1.0063063063063062 | 0.81864757080895378 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 0.83333333333333337 | True |
| default-small-dynamic50 | Query | True | 1236 | 1216 | 1.0164473684210527 | 0.94623268607354538 | False |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 0.99422991566799845 | False |
| default-small-dynamic90 | Query | True | 1295 | 1268.5 | 1.02089081592432 | 1.026240330754868 | False |
| default-small-dynamic90 | Consume | False | 140 | 139 | 1.0071942446043165 | 1.0044217687074828 | False |
| character-small-hair16-clustered | Query | True | 4188 | 4611 | 0.90826284970722182 | 0.93446435379336668 | True |
| character-small-hair16-clustered | Consume | False | 118 | 117 | 1.0085470085470085 | 1.0078339208773992 | False |
| character-small-hair16-interleaved | Query | True | 4241 | 4591 | 0.92376388586364622 | 0.96284720521236455 | True |
| character-small-hair16-interleaved | Consume | False | 116 | 115 | 1.008695652173913 | 1.0697580645161289 | False |
| updates-small-sparse | MaintenanceAndQuery | True | 787 | 841 | 0.9357907253269917 | 0.9720164220659715 | True |
| updates-small-sparse | FrameTotal | True | 2064 | 2125 | 0.97129411764705886 | 0.96602214427481881 | True |
| character-space-small-interleaved | Query | True | 21950.5 | 22374.5 | 0.98104985586270088 | 0.96883580171084827 | True |
| character-space-small-interleaved | Consume | False | 19715.5 | 19758.5 | 0.9978237214363439 | 1.0066842121355375 | False |
| finite-middle-mask1-oneway | Query | True | 35140.5 | 32882.5 | 1.0686687447730556 | 1.0519090483201714 | False |
| finite-middle-mask1-oneway | Consume | False | 67 | 67 | 1 | 0.97170971709717147 | True |
| finite-middle-mask1-mutual | Query | True | 34984.5 | 32420.5 | 1.0790857636372049 | 1.0708012816355124 | False |
| finite-middle-mask1-mutual | Consume | False | 6 | 5 | 1.2 | 1.125 | False |
| finite-middle-mask4-oneway | Query | True | 40024 | 34687.5 | 1.153845045045045 | 1.1425525216867212 | False |
| finite-middle-mask4-oneway | Consume | False | 243 | 243 | 1 | 0.83475145544111051 | True |
| finite-middle-mask4-mutual | Query | True | 37833.5 | 35152.5 | 1.076267690775905 | 1.0839895893424918 | False |
| finite-middle-mask4-mutual | Consume | False | 127 | 127 | 1 | 1.1347517730496455 | False |
| finite-middle-mask8-oneway | Query | True | 33693 | 32604.5 | 1.0333849621984694 | 1.0356398925470607 | False |
| finite-middle-mask8-oneway | Consume | False | 461 | 461 | 1 | 1.0132662793722362 | False |
| finite-middle-mask8-mutual | Query | True | 33610 | 32454 | 1.0356196462685647 | 1.0263246080558357 | False |
| finite-middle-mask8-mutual | Consume | False | 461.5 | 460 | 1.0032608695652174 | 1.0661119239209953 | False |
| default-middle-dynamic10 | Query | True | 15690 | 15549 | 1.0090681072737797 | 0.98260749918028978 | False |
| default-middle-dynamic10 | Consume | False | 458 | 458.5 | 0.99890948745910579 | 0.97050173010380614 | True |
| default-middle-dynamic50 | Query | True | 17228.5 | 16977 | 1.0148141603345704 | 1.0173979210544193 | False |
| default-middle-dynamic50 | Consume | False | 1742 | 1742 | 1 | 1.0207126557837456 | False |
| default-middle-dynamic90 | Query | True | 17931 | 17585 | 1.0196758601080467 | 1.0258686899557514 | False |
| default-middle-dynamic90 | Consume | False | 2301 | 2299 | 1.0008699434536754 | 0.98969345610245929 | False |
| finite-large-mask1-oneway | Query | True | 283049.5 | 276405.5 | 1.0240371483201312 | 1.0211301665737413 | False |
| finite-large-mask1-oneway | Consume | False | 599 | 616 | 0.97240259740259738 | 1.0050908622245134 | False |
| finite-large-mask1-mutual | Query | True | 279231 | 269416.5 | 1.0364287265256582 | 1.0317450309142229 | False |
| finite-large-mask1-mutual | Consume | False | 31 | 28 | 1.1071428571428572 | 1.027027027027027 | False |
| finite-large-mask4-oneway | Query | True | 299188.5 | 296127 | 1.0103384696430924 | 1.0073361854390621 | False |
| finite-large-mask4-oneway | Consume | False | 2305.5 | 2253 | 1.0233022636484688 | 1.0489981012774903 | False |
| finite-large-mask4-mutual | Query | True | 300263.5 | 297342.5 | 1.0098236881710485 | 1.0141899430825023 | False |
| finite-large-mask4-mutual | Consume | False | 1112.5 | 1114 | 0.99865350089766602 | 0.97882370272161434 | True |
| finite-large-mask8-oneway | Query | True | 279486.5 | 273301.5 | 1.0226306844272717 | 1.0287260769904729 | False |
| finite-large-mask8-oneway | Consume | False | 4515.5 | 4431 | 1.0190701873166328 | 1.000954315680068 | False |
| finite-large-mask8-mutual | Query | True | 279086 | 271847.5 | 1.0266270611280222 | 1.0148948542717098 | False |
| finite-large-mask8-mutual | Consume | False | 4448.5 | 4363 | 1.0195966078386431 | 0.99670184696569919 | False |
| default-large-dynamic10 | Query | True | 247124.5 | 246917 | 1.0008403633609675 | 1.0088147127958531 | False |
| default-large-dynamic10 | Consume | False | 8086 | 8019.5 | 1.0082922875490992 | 1.0217277401185449 | False |
| default-large-dynamic50 | Query | True | 272984.5 | 271766 | 1.0044836366580072 | 1.0116933564369632 | False |
| default-large-dynamic50 | Consume | False | 33115 | 33368 | 0.99241788539918485 | 0.9921197643023687 | True |
| default-large-dynamic90 | Query | True | 285290 | 282213 | 1.0109031121883116 | 1.0217695902435691 | False |
| default-large-dynamic90 | Consume | False | 44295.5 | 44017.5 | 1.0063156699040154 | 1.0040610234072731 | False |
| character-large-hair64-clustered | Query | True | 998150 | 994602.5 | 1.003566751541445 | 0.9907583267199026 | False |
| character-large-hair64-clustered | Consume | False | 43452.5 | 43509.5 | 0.99868994127719235 | 1.0182996295026849 | False |

Required metrics: 5/33 pass. Case reports: 32.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
