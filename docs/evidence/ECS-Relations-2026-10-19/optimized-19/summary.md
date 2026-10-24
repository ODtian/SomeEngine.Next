# optimized-19 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4478 | 3898.5 | 1.1486469154803129 | 1.183485725157521 | False |
| finite-small-mask1-oneway | Consume | False | 10 | 10 | 1 | 1 | True |
| finite-small-mask1-mutual | Query | True | 4361 | 4184 | 1.0423040152963672 | 1.0481764389456698 | False |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1.1624999999999943 | False |
| finite-small-mask4-oneway | Query | True | 4222 | 4415.5 | 0.95617710338579998 | 1.0309325663933422 | False |
| finite-small-mask4-oneway | Consume | False | 30 | 31 | 0.967741935483871 | 1.0588235294117647 | False |
| finite-small-mask4-mutual | Query | True | 4263.5 | 4509 | 0.9455533377689066 | 0.91956652385475823 | True |
| finite-small-mask4-mutual | Consume | False | 16 | 17 | 0.94117647058823528 | 0.94999999999999996 | True |
| finite-small-mask8-oneway | Query | True | 3969.5 | 4289 | 0.92550711121473539 | 0.91439966543243867 | True |
| finite-small-mask8-oneway | Consume | False | 58 | 58 | 1 | 0.95454545454545459 | True |
| finite-small-mask8-mutual | Query | True | 3980.5 | 4154.5 | 0.95811770369478877 | 0.97223372490900761 | True |
| finite-small-mask8-mutual | Consume | False | 59 | 58 | 1.0172413793103448 | 1.015625 | False |
| default-small-dynamic10 | Query | True | 1118.5 | 1135.5 | 0.98502862175253192 | 1.0005188644281373 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1 | True |
| default-small-dynamic50 | Query | True | 1243 | 1260.5 | 0.9861166203887346 | 1.0131834303687346 | False |
| default-small-dynamic50 | Consume | False | 106 | 106 | 1 | 1.0238738738738737 | False |
| default-small-dynamic90 | Query | True | 1290 | 1306 | 0.98774885145482394 | 0.98489283353790957 | True |
| default-small-dynamic90 | Consume | False | 140 | 140 | 1 | 0.98013245033112584 | True |
| character-small-hair16-clustered | Query | True | 4246.5 | 4424 | 0.95987793851717906 | 0.92456274945859596 | True |
| character-small-hair16-clustered | Consume | False | 119 | 119 | 1 | 1 | True |
| finite-middle-mask1-oneway | Query | True | 31956.5 | 31678.5 | 1.0087756680398376 | 1.0388885701381563 | False |
| finite-middle-mask1-oneway | Consume | False | 68 | 68 | 1 | 0.94373865698729609 | True |
| finite-middle-mask1-mutual | Query | True | 32138 | 31643.5 | 1.0156272220203202 | 1.008790660068833 | False |
| finite-middle-mask1-mutual | Consume | False | 6 | 5 | 1.2 | 0.96818181818181615 | False |
| finite-middle-mask4-oneway | Query | True | 37838.5 | 38118.5 | 0.99265448535488021 | 1.0023486213446224 | False |
| finite-middle-mask4-oneway | Consume | False | 242 | 240 | 1.0083333333333333 | 0.9996694214876044 | False |
| finite-middle-mask4-mutual | Query | True | 33638.5 | 33117 | 1.0157471993236102 | 1.0018960739705685 | False |
| finite-middle-mask4-mutual | Consume | False | 127 | 127 | 1 | 0.9933075026417757 | True |
| finite-middle-mask8-oneway | Query | True | 32179 | 31847.5 | 1.0104089802967267 | 1.0065651873346044 | False |
| finite-middle-mask8-oneway | Consume | False | 460 | 458 | 1.0043668122270741 | 1.099319599319601 | False |
| finite-middle-mask8-mutual | Query | True | 39188 | 40001.5 | 0.97966326262765147 | 0.9985008136406982 | True |
| finite-middle-mask8-mutual | Consume | False | 466 | 461 | 1.0108459869848156 | 1.0362131401965857 | False |
| default-middle-dynamic10 | Query | True | 15570.5 | 15888.5 | 0.97998552412121975 | 0.99494664300366442 | True |
| default-middle-dynamic10 | Consume | False | 458 | 459 | 0.9978213507625272 | 0.99715244584562168 | True |
| default-middle-dynamic50 | Query | True | 17303 | 17456 | 0.99123510540788262 | 1.0035292589086611 | False |
| default-middle-dynamic50 | Consume | False | 1746 | 1747 | 0.99942759015455063 | 0.9716133407610148 | True |
| default-middle-dynamic90 | Query | True | 18029 | 18211 | 0.99000604030531003 | 0.9878263201434937 | True |
| default-middle-dynamic90 | Consume | False | 2302 | 2299 | 1.0013049151805133 | 1.0409713743047075 | False |
| character-middle-hair64-interleaved | Query | True | 58657.5 | 60179.5 | 0.97470899558819868 | 0.99627922926553303 | True |
| character-middle-hair64-interleaved | Consume | False | 2040 | 2032.5 | 1.003690036900369 | 1.0180821572580645 | False |
| finite-large-mask1-oneway | Query | True | 264766.5 | 257616.5 | 1.0277544334310884 | 1.005883275018826 | False |
| finite-large-mask1-oneway | Consume | False | 600.5 | 599 | 1.0025041736227045 | 0.99599173294920795 | False |
| finite-large-mask1-mutual | Query | True | 264631 | 274327.5 | 0.96465356189226381 | 0.99076963726861755 | True |
| finite-large-mask1-mutual | Consume | False | 29 | 28 | 1.0357142857142858 | 1.0678210678210684 | False |
| finite-large-mask4-oneway | Query | True | 281306.5 | 288020 | 0.97669085480174989 | 0.99481659067787032 | True |
| finite-large-mask4-oneway | Consume | False | 2176 | 2130 | 1.0215962441314554 | 1.0344756631374161 | False |
| finite-large-mask4-mutual | Query | True | 285242.5 | 279824.5 | 1.0193621359101865 | 1.031871658303154 | False |
| finite-large-mask4-mutual | Consume | False | 1205 | 1148.5 | 1.0491946016543316 | 1.0312264995523723 | False |
| finite-large-mask8-oneway | Query | True | 266003.5 | 257599.5 | 1.0326242869260227 | 1.0276282968953914 | False |
| finite-large-mask8-oneway | Consume | False | 4239.5 | 4324 | 0.98045790934320076 | 0.97926297345660618 | True |
| finite-large-mask8-mutual | Query | True | 263207 | 262503 | 1.002681874111915 | 1.0058826791307718 | False |
| finite-large-mask8-mutual | Consume | False | 4516 | 4408 | 1.0245009074410163 | 0.98080494217580783 | False |
| default-large-dynamic10 | Query | True | 246737.5 | 251943 | 0.97933858055195022 | 0.98073077688120402 | True |
| default-large-dynamic10 | Consume | False | 8126.5 | 8215.5 | 0.98916681881808777 | 0.96980955829489324 | True |
| default-large-dynamic50 | Query | True | 273596.5 | 279237 | 0.97980031299577064 | 0.97669452695804304 | True |
| default-large-dynamic50 | Consume | False | 33808 | 33581.5 | 1.0067447850751157 | 0.99494149395273634 | False |
| default-large-dynamic90 | Query | True | 282480.5 | 290161 | 0.97353021253717764 | 0.97365343460477127 | True |
| default-large-dynamic90 | Consume | False | 43928 | 44122.5 | 0.99559181823332765 | 0.9956518398032761 | True |
| character-large-hair64-clustered | Query | True | 974046 | 999814.5 | 0.97422671905638492 | 0.98822111307769123 | True |
| character-large-hair64-clustered | Consume | False | 43374.5 | 43091.5 | 1.0065674204889596 | 1.003837161667728 | False |

Required metrics: 15/30 pass. Case reports: 30.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
