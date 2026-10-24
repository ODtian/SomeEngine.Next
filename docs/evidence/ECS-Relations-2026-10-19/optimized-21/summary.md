# optimized-21 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4013 | 3951.5 | 1.0155637099835506 | 1.0317904007631469 | False |
| finite-small-mask1-oneway | Consume | False | 10 | 10 | 1 | 1 | True |
| finite-small-mask1-mutual | Query | True | 4042 | 3989.5 | 1.0131595438024814 | 1.0237118816417292 | False |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1.3333333333333333 | False |
| finite-small-mask4-oneway | Query | True | 4100.5 | 4155.5 | 0.98676452893755262 | 0.95587019537667839 | True |
| finite-small-mask4-oneway | Consume | False | 31 | 31 | 1 | 0.94285714285714284 | True |
| finite-small-mask4-mutual | Query | True | 4263 | 4453 | 0.95733213563889508 | 1.0840169133192383 | False |
| finite-small-mask4-mutual | Consume | False | 16 | 17 | 0.94117647058823528 | 0.967741935483871 | True |
| finite-small-mask8-oneway | Query | True | 3941.5 | 3961.5 | 0.99495140729521647 | 1.0308204214701038 | False |
| finite-small-mask8-oneway | Consume | False | 60 | 59 | 1.0169491525423728 | 1.0363495746326377 | False |
| finite-small-mask8-mutual | Query | True | 4065 | 4047 | 1.0044477390659747 | 1.0228655501227901 | False |
| finite-small-mask8-mutual | Consume | False | 59 | 59 | 1 | 1 | True |
| default-small-dynamic10 | Query | True | 1140 | 1166.5 | 0.97728246892413206 | 1.0403728909683814 | False |
| default-small-dynamic10 | Consume | False | 29 | 28.5 | 1.0175438596491229 | 1.0333333333333334 | False |
| default-small-dynamic50 | Query | True | 1269 | 1266 | 1.0023696682464456 | 1.0195567144719686 | False |
| default-small-dynamic50 | Consume | False | 107 | 106 | 1.0094339622641511 | 1.0175438596491229 | False |
| default-small-dynamic90 | Query | True | 1282 | 1304 | 0.98312883435582821 | 1.0321154596281366 | False |
| default-small-dynamic90 | Consume | False | 140 | 139 | 1.0071942446043165 | 1.049324324324324 | False |
| character-small-hair16-clustered | Query | True | 4169 | 4576.5 | 0.91095815579591388 | 0.89736584995684054 | True |
| character-small-hair16-clustered | Consume | False | 117 | 117 | 1 | 0.98274568642160576 | True |
| finite-middle-mask1-oneway | Query | True | 32502 | 32060 | 1.0137866500311916 | 0.98856883634304205 | False |
| finite-middle-mask1-oneway | Consume | False | 70 | 70 | 1 | 1.0153424657534256 | False |
| finite-middle-mask1-mutual | Query | True | 32371 | 31820 | 1.0173161533626649 | 1.0269208116299804 | False |
| finite-middle-mask1-mutual | Consume | False | 6 | 5 | 1.2 | 1.2222222222222223 | False |
| finite-middle-mask4-oneway | Query | True | 32954.5 | 33251 | 0.99108297494812181 | 0.98250618146337143 | True |
| finite-middle-mask4-oneway | Consume | False | 245 | 245.5 | 0.99796334012219956 | 1.0392554367858458 | False |
| finite-middle-mask4-mutual | Query | True | 34596 | 33332.5 | 1.0379059476486912 | 1.019299521681305 | False |
| finite-middle-mask4-mutual | Consume | False | 126 | 126 | 1 | 1.0260714285714283 | False |
| finite-middle-mask8-oneway | Query | True | 31592.5 | 31257.5 | 1.0107174278173239 | 0.99171417294260367 | False |
| finite-middle-mask8-oneway | Consume | False | 471 | 470.5 | 1.0010626992561105 | 0.97995086773912354 | False |
| finite-middle-mask8-mutual | Query | True | 31711 | 31319.5 | 1.0125001995561871 | 1.0145973946759483 | False |
| finite-middle-mask8-mutual | Consume | False | 471 | 470 | 1.0021276595744681 | 0.93817743877960691 | False |
| default-middle-dynamic10 | Query | True | 15649 | 15904 | 0.98396629778672029 | 0.98500756525391053 | True |
| default-middle-dynamic10 | Consume | False | 460 | 460 | 1 | 0.99991379310344819 | True |
| default-middle-dynamic50 | Query | True | 17273 | 17541 | 0.98472150960606575 | 0.9903772314619036 | True |
| default-middle-dynamic50 | Consume | False | 1745 | 1745 | 1 | 0.93821832340385614 | True |
| default-middle-dynamic90 | Query | True | 17856 | 18171.5 | 0.98263764686459565 | 0.97474761422951106 | True |
| default-middle-dynamic90 | Consume | False | 2300 | 2298 | 1.0008703220191471 | 1.0313273198386876 | False |
| character-middle-hair64-interleaved | Query | True | 57203.5 | 61271.5 | 0.93360697877479737 | 0.95700448287523099 | True |
| character-middle-hair64-interleaved | Consume | False | 2086.5 | 2070 | 1.0079710144927536 | 1.0259142010633548 | False |
| finite-large-mask1-oneway | Query | True | 274977.5 | 280403.5 | 0.98064931429172608 | 0.98060180528883734 | True |
| finite-large-mask1-oneway | Consume | False | 609.5 | 607.5 | 1.0032921810699589 | 0.96170645605217497 | False |
| finite-large-mask1-mutual | Query | True | 264643.5 | 260420 | 1.0162180324091852 | 1.0104064929637109 | False |
| finite-large-mask1-mutual | Consume | False | 27 | 26 | 1.0384615384615385 | 1 | False |
| finite-large-mask4-oneway | Query | True | 277611 | 274947 | 1.009689140088817 | 1.013162680830354 | False |
| finite-large-mask4-oneway | Consume | False | 2212 | 2213 | 0.99954812471757792 | 0.99270552191990657 | True |
| finite-large-mask4-mutual | Query | True | 291262 | 288919.5 | 1.0081077947317505 | 1.0382534380790407 | False |
| finite-large-mask4-mutual | Consume | False | 1169 | 1163 | 1.0051590713671539 | 0.9757581803671187 | False |
| finite-large-mask8-oneway | Query | True | 263146.5 | 260257.5 | 1.0111005446528918 | 1.0125436805282972 | False |
| finite-large-mask8-oneway | Consume | False | 4346.5 | 4485 | 0.96911928651059087 | 0.99046294643276633 | True |
| finite-large-mask8-mutual | Query | True | 273897.5 | 262001.5 | 1.0454043202042735 | 1.0593492909586495 | False |
| finite-large-mask8-mutual | Consume | False | 4461 | 4400.5 | 1.0137484376775368 | 1.0007371216345202 | False |
| default-large-dynamic10 | Query | True | 246251.5 | 251317.5 | 0.97984223144030957 | 0.96651558236589286 | True |
| default-large-dynamic10 | Consume | False | 7975 | 7932.5 | 1.005357705641349 | 0.98703556128424952 | False |
| default-large-dynamic50 | Query | True | 272263.5 | 280585 | 0.97034232050893665 | 0.96459932226931755 | True |
| default-large-dynamic50 | Consume | False | 33069.5 | 33418.5 | 0.98955668267576347 | 0.99095427152627358 | True |
| default-large-dynamic90 | Query | True | 283071 | 290433 | 0.97465164082593925 | 0.97013390192662707 | True |
| default-large-dynamic90 | Consume | False | 44155.5 | 44255 | 0.997751666478364 | 0.99067554590097251 | True |
| character-large-hair64-clustered | Query | True | 1000449.5 | 990458 | 1.0100877573809288 | 1.0099960420056819 | False |
| character-large-hair64-clustered | Consume | False | 43208 | 43094 | 1.002645379867267 | 1.0152192516283758 | False |

Required metrics: 11/30 pass. Case reports: 30.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
