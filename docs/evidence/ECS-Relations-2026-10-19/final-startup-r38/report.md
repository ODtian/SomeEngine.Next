# Final same-source process startup

Source: 51c8ce75949cf16d7190e14028c655f526d5697b. NativeAOT, Fixed machine, seven pairs per case with alternating launch order. Each fresh process performs its real first-result query and consumption. These process startup distributions are reported separately from warm-query acceptance.

| Case | Boundary | E/H samples | Median E/H ticks | Median ratio | p95 E/H ticks | p95 ratio |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| finite-middle-mask4-mutual | ProcessToFirstResultTicks | 7/7 | 915046/879401 | 1.04053327207952 | 1917614/921743.8 | 2.08041974353394 |
| finite-middle-mask4-mutual | ProcessToExitTicks | 7/7 | 977424/946250 | 1.03294478203435 | 1982858.6/1006466.9 | 1.97011804362369 |
| default-middle-dynamic50 | ProcessToFirstResultTicks | 7/7 | 859758/872631 | 0.985248060176638 | 1772772.6/1538559 | 1.15222919628042 |
| default-middle-dynamic50 | ProcessToExitTicks | 7/7 | 937610/947267 | 0.989805408612355 | 1847975/1649781.7 | 1.12013304548111 |
| character-middle-hair16-clustered | ProcessToFirstResultTicks | 7/7 | 894037/870112 | 1.02749646022581 | 1179549.3/1813452.9 | 0.650443857681664 |
| character-middle-hair16-clustered | ProcessToExitTicks | 7/7 | 996748/933376 | 1.06789546763577 | 1297155.6/1885527.1 | 0.687953835296242 |
| bitmap-space-middle-uniform-mutual | ProcessToFirstResultTicks | 7/7 | 937752/991815 | 0.945490842546241 | 1031991/2199304.2 | 0.469235224485999 |
| bitmap-space-middle-uniform-mutual | ProcessToExitTicks | 7/7 | 1032339/1061862 | 0.972196952146324 | 1089935.3/2314861.9 | 0.47084247228744 |
| character-space-middle-clustered | ProcessToFirstResultTicks | 7/7 | 1073910/1126264 | 0.95351533920999 | 1205839.6/2366178 | 0.50961491485425 |
| character-space-middle-clustered | ProcessToExitTicks | 7/7 | 1138069/1217352 | 0.934872575886021 | 1310594.1/2439973.3 | 0.537134607169677 |
| complete-middle-uniform | ProcessToFirstResultTicks | 7/7 | 1008130/1002926 | 1.00518881751994 | 2057820.3/1089085 | 1.88949466754202 |
| complete-middle-uniform | ProcessToExitTicks | 7/7 | 1129751/1121707 | 1.00717121315994 | 2124166.2/1203080.9 | 1.76560545512775 |
| updates-middle-uniform | ProcessToFirstResultTicks | 7/7 | 1010145/994171 | 1.0160676583807 | 1078583/2332503.4 | 0.462414331314587 |
| updates-middle-uniform | ProcessToExitTicks | 7/7 | 1082823/1097619 | 0.986519912647285 | 1130118.7/2434163.7 | 0.464273910583746 |
| threads-middle-uniform-parallel | ProcessToFirstResultTicks | 7/7 | 1057831/1057842 | 0.999989601471675 | 2046148.3/1333073.8 | 1.53490999523057 |
| threads-middle-uniform-parallel | ProcessToExitTicks | 7/7 | 1183902/1143954 | 1.03492098458504 | 2256529.1/1446181.5 | 1.56033602974454 |
| consume-middle-uniform | ProcessToFirstResultTicks | 7/7 | 1095924/1068181 | 1.02597219010636 | 1879497.4/1143080.5 | 1.64423887906407 |
| consume-middle-uniform | ProcessToExitTicks | 7/7 | 1163441/1142397 | 1.01842091672159 | 1966721.9/1210146.3 | 1.62519349933144 |
