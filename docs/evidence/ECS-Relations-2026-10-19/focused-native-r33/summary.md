# focused-native-r33 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-mutual | Query | True | 4028 | 3921.5 | 1.0271579752645672 | 1.020297699594046 | False | False |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1 | True | True |
| finite-small-mask8-oneway | Query | True | 3817 | 3861.5 | 0.98847598083646249 | 0.99120472167573193 | True | True |
| finite-small-mask8-oneway | Consume | False | 59 | 59 | 1 | 1.0153846153846153 | False | False |
| default-small-dynamic10 | Query | True | 1054 | 1036 | 1.0173745173745175 | 1.0054336468129572 | False | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1.0086206896551724 | False | True |
| default-small-dynamic50 | Query | True | 1149 | 1158 | 0.99222797927461137 | 1.0166163141993958 | False | False |
| default-small-dynamic50 | Consume | False | 104 | 104 | 1 | 1.0113636363636365 | False | False |
| default-small-dynamic90 | Query | True | 1194 | 1214 | 0.9835255354200988 | 1.0058910162002945 | False | True |
| default-small-dynamic90 | Consume | False | 139 | 139 | 1 | 0.99493243243243246 | True | True |
| threads-small-sparse-parallel | ScheduledPairOutput | True | 859 | 865.5 | 0.9924898902368573 | 0.95709329925141495 | True | True |
| threads-small-sparse-parallel | Consume | False | 489 | 485.5 | 1.0072090628218331 | 0.85866130422292442 | False | True |
| finite-middle-mask8-oneway | Query | True | 31309 | 32727.5 | 0.95665724543579556 | 0.95136014614791764 | True | True |
| finite-middle-mask8-oneway | Consume | False | 469 | 469 | 1 | 0.9128160418482999 | True | True |
| default-middle-dynamic10 | Query | True | 16431.5 | 16251.5 | 1.0110759006860905 | 1.0003347607158299 | False | False |
| default-middle-dynamic10 | Consume | False | 471.5 | 471 | 1.0010615711252655 | 1.008656224237428 | False | True |
| default-middle-dynamic50 | Query | True | 17378 | 17529 | 0.9913857036910263 | 1.0117702757795342 | False | False |
| default-middle-dynamic50 | Consume | False | 1748 | 1748 | 1 | 1.008108108108108 | False | True |
| default-middle-dynamic90 | Query | True | 17755 | 18186 | 0.9763004508962938 | 0.99164482306684143 | True | True |
| default-middle-dynamic90 | Consume | False | 2299 | 2299 | 1 | 0.94540491355777978 | True | True |
| finite-large-mask8-oneway | Query | True | 259374 | 272841.5 | 0.9506398403468681 | 0.94963746902257262 | True | True |
| finite-large-mask8-oneway | Consume | False | 4179 | 4206 | 0.99358059914407992 | 1.0203622077424301 | False | False |
| finite-large-mask8-mutual | Query | True | 260521 | 261347.5 | 0.99683754388314405 | 0.99829284354936154 | True | True |
| finite-large-mask8-mutual | Consume | False | 4438.5 | 4417 | 1.004867557165497 | 0.97757648417032994 | False | True |
| default-large-dynamic10 | Query | True | 249702 | 247777.5 | 1.0077670490661985 | 1.0080699165657643 | False | True |
| default-large-dynamic10 | Consume | False | 7890.5 | 7923.5 | 0.99583517384994003 | 1.0120438760886341 | False | False |
| default-large-dynamic50 | Query | True | 277829.5 | 281582 | 0.98667350896008976 | 0.99558703423084149 | True | True |
| default-large-dynamic50 | Consume | False | 33709 | 33905.5 | 0.99420448009909901 | 0.99560365689701535 | True | True |
| default-large-dynamic90 | Query | True | 284534 | 292154.5 | 0.97391619844979282 | 0.97425826801680859 | True | True |
| default-large-dynamic90 | Consume | False | 43976.5 | 44105 | 0.99708649812946382 | 0.98822073255082243 | True | True |
| character-large-hair64-clustered | Query | True | 959819 | 1030132.5 | 0.93174324662118713 | 0.93049914092924568 | True | True |
| character-large-hair64-clustered | Consume | False | 43081 | 43018.5 | 1.0014528633029975 | 1.0054405838143994 | False | True |

Required metrics: 9/16 pass <=1.00; 11/16 pass <=1.01. Case reports: 16.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
