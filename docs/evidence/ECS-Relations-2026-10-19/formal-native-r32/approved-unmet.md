# formal-native-r32 approved ratio results

ApprovedRatioLimit=1.01; StrictRatioLimit=1.00; cases=108; equivalent=108; required=126; strict passed=104; approved passed=110; approved unmet=16. Native Passed remains the original strict result. Allocation columns contain full-frame bytes summed over each side''s 256 samples. H1/H2 controls are a separate diagnostic series.

| Case | Boundary | E median ticks | H median ticks | Median ratio | E p95 ticks | H p95 ticks | P95 ratio | E frame bytes | H frame bytes | E GC0/1/2 | H GC0/1/2 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| finite-small-mask1-mutual | Query | 3960 | 3959 | 1.0002525890376357 | 4818 | 4621.75 | 1.0424622707848759 | 0 | 0 | 0/0/0 | 0/0/0 |
| finite-small-mask8-oneway | Query | 4198 | 4152 | 1.0110789980732178 | 4847.75 | 4559 | 1.063336257951305 | 0 | 0 | 0/0/0 | 0/0/0 |
| default-small-dynamic10 | Query | 1064 | 1039 | 1.0240615976900866 | 1244 | 1240.5 | 1.0028214429665459 | 0 | 0 | 0/0/0 | 0/0/0 |
| default-small-dynamic50 | Query | 1182 | 1142 | 1.0350262697022767 | 1391 | 1286 | 1.0816485225505443 | 0 | 0 | 0/0/0 | 0/0/0 |
| default-small-dynamic90 | Query | 1262.5 | 1228 | 1.0280944625407167 | 1555 | 1577.75 | 0.98558073205514185 | 0 | 0 | 0/0/0 | 0/0/0 |
| threads-small-sparse-parallel | ScheduledPairOutput | 819 | 803 | 1.0199252801992529 | 1206.25 | 1222.25 | 0.98690938842299036 | 947592 | 955472 | 0/0/0 | 0/0/0 |
| finite-middle-mask8-oneway | Query | 32742.5 | 32094 | 1.0202062690845641 | 35765.75 | 34942.5 | 1.0235601345066896 | 0 | 0 | 0/0/0 | 0/0/0 |
| default-middle-dynamic10 | Query | 16055.5 | 15652.5 | 1.025746685832934 | 17193.25 | 17199.5 | 0.99963661734352738 | 0 | 0 | 0/0/0 | 0/0/0 |
| default-middle-dynamic50 | Query | 17554 | 16935.5 | 1.0365209175991261 | 18913 | 18411.75 | 1.0272244626393472 | 0 | 0 | 0/0/0 | 0/0/0 |
| default-middle-dynamic90 | Query | 18290.5 | 17766 | 1.0295226837780029 | 20164.5 | 19407.75 | 1.0389921551957337 | 0 | 0 | 0/0/0 | 0/0/0 |
| finite-large-mask8-oneway | Query | 267563.5 | 264907 | 1.0100280475789616 | 276070.75 | 276493 | 0.99847283656367436 | 0 | 0 | 0/0/0 | 0/0/0 |
| finite-large-mask8-mutual | Query | 272162.5 | 270002 | 1.0080017925793143 | 287912.25 | 282319.5 | 1.0198100024971708 | 0 | 0 | 0/0/0 | 0/0/0 |
| default-large-dynamic10 | Query | 253635.5 | 249197 | 1.0178112096052521 | 264185.5 | 261725.5 | 1.0093991605709034 | 0 | 0 | 0/0/0 | 0/0/0 |
| default-large-dynamic50 | Query | 285360.5 | 277246 | 1.029268231101621 | 301740 | 294341 | 1.0251375105744698 | 0 | 0 | 0/0/0 | 0/0/0 |
| default-large-dynamic90 | Query | 306109 | 299147 | 1.0232728391058576 | 321907.5 | 313405.5 | 1.0271277945026491 | 0 | 0 | 0/0/0 | 0/0/0 |
| character-large-hair64-clustered | Query | 1046400 | 1023415.5 | 1.0224586201791941 | 1085565.25 | 1071559.5 | 1.0130704361260388 | 2543616 | 2543616 | 0/0/0 | 0/0/0 |
