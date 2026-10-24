# optimized-11 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask4-mutual | Query | True | 4539 | 4476 | 1.0140750670241288 | 1.0183468100670212 | False |
| finite-small-mask4-mutual | Consume | False | 16 | 17 | 0.94117647058823528 | 1 | True |
| default-small-dynamic10 | Query | True | 1189.5 | 1122.5 | 1.0596881959910913 | 0.96437062585808542 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1 | True |
| default-small-dynamic50 | Query | True | 1318 | 1233 | 1.0689375506893755 | 1.0445733878412238 | False |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 0.99437959360138373 | False |
| default-small-dynamic90 | Query | True | 1337 | 1270 | 1.052755905511811 | 0.99099475766249634 | False |
| default-small-dynamic90 | Consume | False | 140 | 139 | 1.0071942446043165 | 0.99081632653061213 | False |
| character-small-hair16-clustered | Query | True | 4593 | 4479.5 | 1.0253376492912156 | 1.0196086044886232 | False |
| character-small-hair16-clustered | Consume | False | 119 | 118 | 1.0084745762711864 | 0.80733944954128778 | False |
| character-small-hair16-interleaved | Query | True | 4693 | 4593 | 1.0217722621380361 | 1.1254036348070464 | False |
| character-small-hair16-interleaved | Consume | False | 120 | 121 | 0.99173553719008267 | 1.6772058823529388 | False |
| character-small-hair64-clustered | Query | True | 3163.5 | 3057.5 | 1.0346688470973018 | 1.0577638869157551 | False |
| character-small-hair64-clustered | Consume | False | 91 | 92 | 0.98913043478260865 | 0.96999999999999997 | True |
| character-small-hair64-interleaved | Query | True | 3287.5 | 3184.5 | 1.0323441670591931 | 1.0067320357090592 | False |
| character-small-hair64-interleaved | Consume | False | 95 | 95 | 1 | 0.98726415094339603 | True |
| character-space-small-clustered | Query | True | 16269.5 | 17486 | 0.93043005833238024 | 0.94317741335801486 | True |
| character-space-small-clustered | Consume | False | 14797 | 14835.5 | 0.99740487344545181 | 1.0075376025701175 | False |
| character-space-small-interleaved | Query | True | 21866 | 22491.5 | 0.97218949380877218 | 0.98174773626128764 | True |
| character-space-small-interleaved | Consume | False | 17663.5 | 17656.5 | 1.0003964545634751 | 0.9955429172267154 | False |
| character-middle-hair16-clustered | Query | True | 71688.5 | 69482 | 1.0317564261247518 | 1.0289759969850261 | False |
| character-middle-hair16-clustered | Consume | False | 2167.5 | 2146 | 1.0100186393289841 | 1.0210473565522424 | False |
| character-middle-hair16-interleaved | Query | True | 73365 | 70903.5 | 1.0347161987772113 | 1.0319554382878924 | False |
| character-middle-hair16-interleaved | Consume | False | 2170 | 2142 | 1.0130718954248366 | 1.0309570982244913 | False |
| character-middle-hair64-clustered | Query | True | 59975 | 60771.5 | 0.98689352739359737 | 1.0167202753706521 | False |
| character-middle-hair64-clustered | Consume | False | 2074.5 | 2045 | 1.0144254278728606 | 1.0115080266936634 | False |
| character-middle-hair64-interleaved | Query | True | 60646 | 60552 | 1.0015523847271766 | 1.0104828238072285 | False |
| character-middle-hair64-interleaved | Consume | False | 2038.5 | 2032.5 | 1.0029520295202952 | 1.0362332436883692 | False |
| character-space-middle-clustered | Query | True | 64860.5 | 70765.5 | 0.91655538362620204 | 0.91579556335780399 | True |
| character-space-middle-clustered | Consume | False | 59019.5 | 59008 | 1.0001948888286334 | 0.99121628368290238 | False |
| character-space-middle-interleaved | Query | True | 87611.5 | 90021.5 | 0.97322861760801582 | 0.97910691490463708 | True |
| character-space-middle-interleaved | Consume | False | 72042 | 73002.5 | 0.98684291633848154 | 0.96552948192159338 | True |
| finite-large-mask4-mutual | Query | True | 301099.5 | 294452.5 | 1.0225740993878469 | 1.0330994012145942 | False |
| finite-large-mask4-mutual | Consume | False | 1210.5 | 1238 | 0.97778675282714056 | 1.0012244240373771 | False |
| default-large-dynamic10 | Query | True | 267554.5 | 247329 | 1.0817756914878562 | 1.0698860631839315 | False |
| default-large-dynamic10 | Consume | False | 8120 | 7954.5 | 1.0208058331761896 | 0.9935301996025232 | False |
| default-large-dynamic50 | Query | True | 291247.5 | 270945.5 | 1.0749301981394783 | 1.0755458814694305 | False |
| default-large-dynamic50 | Consume | False | 33022.5 | 33298.5 | 0.99171133834857428 | 0.99631285198373609 | True |
| default-large-dynamic90 | Query | True | 305609.5 | 281540 | 1.0854922923918449 | 1.0885142222369051 | False |
| default-large-dynamic90 | Consume | False | 44725.5 | 44334 | 1.0088306942752741 | 1.0041902889127392 | False |
| character-large-hair16-clustered | Query | True | 1151430.5 | 1119678.5 | 1.0283581403054538 | 1.0249748063441531 | False |
| character-large-hair16-clustered | Consume | False | 43163 | 43154.5 | 1.0001969667126256 | 1.0071117367542708 | False |
| character-large-hair16-interleaved | Query | True | 1189221 | 1141061 | 1.0422063325273583 | 1.0514553593696856 | False |
| character-large-hair16-interleaved | Consume | False | 43923 | 43676 | 1.0056552797875262 | 1.0100791555050019 | False |
| character-large-hair64-clustered | Query | True | 1042555.5 | 996541 | 1.0461742166152723 | 1.051890510389196 | False |
| character-large-hair64-clustered | Consume | False | 43502 | 43416.5 | 1.0019692973869381 | 1.0020351235358507 | False |
| character-large-hair64-interleaved | Query | True | 1047300 | 1040108.5 | 1.0069141825107668 | 1.0194723220959059 | False |
| character-large-hair64-interleaved | Consume | False | 43742.5 | 43849 | 0.9975712102898584 | 1.0035083663082294 | False |
| character-space-large-clustered | Query | True | 268452 | 282805.5 | 0.94924603658698292 | 0.95424698107060646 | True |
| character-space-large-clustered | Consume | False | 238529.5 | 239574 | 0.99564017798258575 | 0.99552879381705595 | True |
| character-space-large-interleaved | Query | True | 355966.5 | 376544 | 0.94535167204895043 | 0.94757161227775077 | True |
| character-space-large-interleaved | Consume | False | 290817.5 | 289724 | 1.0037742817301984 | 1.0128929806576303 | False |

Required metrics: 6/26 pass. Case reports: 26.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
