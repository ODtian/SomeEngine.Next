# optimized-18 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4100 | 4049.5 | 1.0124706753920236 | 1.0146214382632295 | False |
| finite-small-mask1-oneway | Consume | False | 10 | 10 | 1 | 0.97083333333333144 | True |
| finite-small-mask1-mutual | Query | True | 4191 | 4247 | 0.9868142218036261 | 0.99892446170874827 | True |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1 | True |
| finite-small-mask4-oneway | Query | True | 4319 | 4214.5 | 1.0247953493890141 | 0.9853536641338162 | False |
| finite-small-mask4-oneway | Consume | False | 31 | 31 | 1 | 0.94444444444444442 | True |
| finite-small-mask4-mutual | Query | True | 4361 | 4374 | 0.9970278920896205 | 0.94585072941909276 | True |
| finite-small-mask4-mutual | Consume | False | 16 | 17 | 0.94117647058823528 | 0.94999999999999996 | True |
| finite-small-mask8-oneway | Query | True | 4102 | 4056.5 | 1.0112165660051768 | 0.97119471447207817 | False |
| finite-small-mask8-oneway | Consume | False | 59 | 58 | 1.0172413793103448 | 0.5892857142857143 | False |
| finite-small-mask8-mutual | Query | True | 4389.5 | 4321.5 | 1.0157352771028578 | 0.98242866110997162 | False |
| finite-small-mask8-mutual | Consume | False | 62 | 61 | 1.0163934426229508 | 0.84833005893909652 | False |
| default-small-dynamic10 | Query | True | 1156 | 1151 | 1.0043440486533448 | 0.99764681090193907 | False |
| default-small-dynamic10 | Consume | False | 29 | 29 | 1 | 0.9794628751974731 | True |
| default-small-dynamic50 | Query | True | 1240.5 | 1220 | 1.0168032786885246 | 1.0486944248412138 | False |
| default-small-dynamic50 | Consume | False | 106 | 106 | 1 | 1 | True |
| default-small-dynamic90 | Query | True | 1285 | 1274 | 1.0086342229199372 | 1.0191281286258906 | False |
| default-small-dynamic90 | Consume | False | 140 | 140 | 1 | 1.0091432441584831 | False |
| character-small-hair16-clustered | Query | True | 4198 | 4292 | 0.97809878844361609 | 0.95980235042735063 | True |
| character-small-hair16-clustered | Consume | False | 117 | 117 | 1 | 0.94014216236438453 | True |
| finite-middle-mask1-oneway | Query | True | 34272.5 | 31687.5 | 1.0815779092702169 | 1.1531985077097271 | False |
| finite-middle-mask1-oneway | Consume | False | 68 | 68 | 1 | 1.2727272727272727 | False |
| finite-middle-mask1-mutual | Query | True | 32905.5 | 32251 | 1.0202939443738179 | 1.0326215646959671 | False |
| finite-middle-mask1-mutual | Consume | False | 6 | 5 | 1.2 | 1 | False |
| finite-middle-mask4-oneway | Query | True | 33756.5 | 33649 | 1.0031947457576749 | 1.009445707330358 | False |
| finite-middle-mask4-oneway | Consume | False | 243 | 243 | 1 | 1.1728802894447066 | False |
| finite-middle-mask4-mutual | Query | True | 35726 | 34222.5 | 1.0439330849587261 | 1.0381222986924545 | False |
| finite-middle-mask4-mutual | Consume | False | 128 | 126 | 1.0158730158730158 | 1.042919905373437 | False |
| finite-middle-mask8-oneway | Query | True | 32044 | 31714.5 | 1.0103895694398461 | 1.0284425189191757 | False |
| finite-middle-mask8-oneway | Consume | False | 464 | 461 | 1.0065075921908895 | 1.0173570350494718 | False |
| finite-middle-mask8-mutual | Query | True | 40629 | 32527 | 1.2490853752267348 | 1.2400116983307163 | False |
| finite-middle-mask8-mutual | Consume | False | 467 | 463 | 1.0086393088552916 | 1.0834103317933641 | False |
| default-middle-dynamic10 | Query | True | 15998 | 15973.5 | 1.001533790340251 | 0.98896543415403482 | False |
| default-middle-dynamic10 | Consume | False | 460 | 460.5 | 0.998914223669924 | 0.97027986460827209 | True |
| default-middle-dynamic50 | Query | True | 17271.5 | 17010 | 1.0153733098177542 | 1.007800131704498 | False |
| default-middle-dynamic50 | Consume | False | 1746 | 1747 | 0.99942759015455063 | 0.99045156665984024 | True |
| default-middle-dynamic90 | Query | True | 18110 | 17953 | 1.0087450565365119 | 0.99850497574738439 | False |
| default-middle-dynamic90 | Consume | False | 2301 | 2297 | 1.0017414018284718 | 1.0563912630579295 | False |
| character-middle-hair64-interleaved | Query | True | 56824.5 | 58556.5 | 0.97042172944079652 | 0.97734208970487202 | True |
| character-middle-hair64-interleaved | Consume | False | 2073.5 | 2047.5 | 1.0126984126984127 | 1.0049373722657278 | False |
| finite-large-mask1-oneway | Query | True | 269499.5 | 266046.5 | 1.0129789341336948 | 1.0257120521121674 | False |
| finite-large-mask1-oneway | Consume | False | 634 | 643 | 0.98600311041990674 | 1.0397267999248074 | False |
| finite-large-mask1-mutual | Query | True | 268193.5 | 263504.5 | 1.0177947625182873 | 1.0179412624053545 | False |
| finite-large-mask1-mutual | Consume | False | 28 | 26 | 1.0769230769230769 | 1.0909090909090908 | False |
| finite-large-mask4-oneway | Query | True | 283021.5 | 282658.5 | 1.0012842352167015 | 1.0176612597898109 | False |
| finite-large-mask4-oneway | Consume | False | 2263 | 2263 | 1 | 0.954741070147755 | True |
| finite-large-mask4-mutual | Query | True | 292400.5 | 290458 | 1.006687713886345 | 1.0040079244007563 | False |
| finite-large-mask4-mutual | Consume | False | 1137 | 1143.5 | 0.99431569742020109 | 0.95456252346977066 | True |
| finite-large-mask8-oneway | Query | True | 288087 | 272812 | 1.0559909388150082 | 1.0610517058103319 | False |
| finite-large-mask8-oneway | Consume | False | 4358 | 4351 | 1.0016088255573432 | 1.0263447908947541 | False |
| finite-large-mask8-mutual | Query | True | 266234 | 264458.5 | 1.0067137187876358 | 1.0140065376931771 | False |
| finite-large-mask8-mutual | Consume | False | 4295.5 | 4315.5 | 0.99536554281079825 | 1.0019704889217813 | False |
| default-large-dynamic10 | Query | True | 246580.5 | 245595 | 1.0040127038416906 | 1.0043641722988659 | False |
| default-large-dynamic10 | Consume | False | 8006.5 | 8005 | 1.0001873828856964 | 0.98951476701888097 | False |
| default-large-dynamic50 | Query | True | 273744.5 | 270795 | 1.0108920031758342 | 1.0294193669961198 | False |
| default-large-dynamic50 | Consume | False | 33171 | 33003.5 | 1.0050752192949233 | 0.99793258589431288 | False |
| default-large-dynamic90 | Query | True | 283458 | 281523 | 1.0068733282893405 | 1.0170064718092739 | False |
| default-large-dynamic90 | Consume | False | 44060 | 44413.5 | 0.99204070834318392 | 1.0039999447697481 | False |
| character-large-hair64-clustered | Query | True | 970506 | 993552 | 0.97680443499685976 | 0.96925287715227315 | True |
| character-large-hair64-clustered | Consume | False | 42981 | 43191.5 | 0.99512635588020792 | 0.99534414315973874 | True |

Required metrics: 5/30 pass. Case reports: 30.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
