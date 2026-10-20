# controlled-paired-r28 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4061 | 4063 | 0.99950775289195171 | 1.014865989858692 | False |
| finite-small-mask1-oneway | Consume | False | 10 | 10 | 1 | 1 | True |
| finite-small-mask1-mutual | Query | True | 4076.5 | 4086.5 | 0.99755291814511193 | 1.0189684752305384 | False |
| finite-small-mask1-mutual | Consume | False | 3 | 2 | 1.5 | 1 | False |
| finite-small-mask4-oneway | Query | True | 4252 | 4256.5 | 0.99894279337483849 | 0.99671352369641575 | True |
| finite-small-mask4-oneway | Consume | False | 32 | 31 | 1.032258064516129 | 1.0378681626928479 | False |
| finite-small-mask4-mutual | Query | True | 4292.5 | 4301 | 0.99802371541501977 | 1.0325817968406119 | False |
| finite-small-mask4-mutual | Consume | False | 16.5 | 16 | 1.03125 | 1 | False |
| finite-small-mask8-oneway | Query | True | 4057.5 | 4284.5 | 0.94701832185785972 | 0.99103432725242391 | True |
| finite-small-mask8-oneway | Consume | False | 59 | 59 | 1 | 1 | True |
| finite-small-mask8-mutual | Query | True | 4171.5 | 4149.5 | 1.005301843595614 | 0.99139802819535194 | False |
| finite-small-mask8-mutual | Consume | False | 60 | 60 | 1 | 1.0052513128282075 | False |
| default-small-dynamic10 | Query | True | 1167 | 1136 | 1.0272887323943662 | 1.0292534099066759 | False |
| default-small-dynamic10 | Consume | False | 29 | 28 | 1.0357142857142858 | 1.0216666666666658 | False |
| default-small-dynamic50 | Query | True | 1332 | 1289.5 | 1.0329585110507948 | 0.983515436412146 | False |
| default-small-dynamic50 | Consume | False | 106 | 106 | 1 | 0.9886264216972882 | True |
| default-small-dynamic90 | Query | True | 1339 | 1299.5 | 1.030396306271643 | 1.0340494399136864 | False |
| default-small-dynamic90 | Consume | False | 140 | 140 | 1 | 1.0312080536912751 | False |
| finite-middle-mask1-oneway | Query | True | 31818 | 31976.5 | 0.99504323487561175 | 0.99350182801745757 | True |
| finite-middle-mask1-oneway | Consume | False | 68 | 68 | 1 | 1.027027027027027 | False |
| finite-middle-mask1-mutual | Query | True | 33869 | 31814 | 1.0645942038096436 | 1.0519977369640303 | False |
| finite-middle-mask1-mutual | Consume | False | 6 | 5 | 1.2 | 1.125 | False |
| finite-middle-mask4-oneway | Query | True | 33823 | 34235 | 0.98796553234993423 | 1.0589683391983433 | False |
| finite-middle-mask4-oneway | Consume | False | 248 | 248.5 | 0.99798792756539234 | 0.98588390501319256 | True |
| finite-middle-mask4-mutual | Query | True | 33842 | 34055 | 0.99374541183379828 | 0.97662190465115983 | True |
| finite-middle-mask4-mutual | Consume | False | 128 | 127 | 1.0078740157480315 | 1.0998970133882584 | False |
| finite-middle-mask8-oneway | Query | True | 32464 | 32767 | 0.99075289162877289 | 0.99692908200326169 | True |
| finite-middle-mask8-oneway | Consume | False | 469 | 470.5 | 0.99681190223166849 | 1.0091342988808425 | False |
| finite-middle-mask8-mutual | Query | True | 33058.5 | 32073 | 1.0307267795341877 | 1.0320088380442922 | False |
| finite-middle-mask8-mutual | Consume | False | 465 | 464 | 1.0021551724137931 | 1.0548403707518021 | False |
| default-middle-dynamic10 | Query | True | 16183 | 15825 | 1.0226224328593996 | 1.0550435175601005 | False |
| default-middle-dynamic10 | Consume | False | 460 | 459 | 1.0021786492374727 | 0.99880546075085319 | False |
| default-middle-dynamic50 | Query | True | 18595 | 18078 | 1.0285982962717115 | 1.0165640414431403 | False |
| default-middle-dynamic50 | Consume | False | 1855.5 | 1853 | 1.0013491635186185 | 0.99578097771255603 | False |
| default-middle-dynamic90 | Query | True | 19061 | 18454.5 | 1.0328646129670269 | 1.0251406521547026 | False |
| default-middle-dynamic90 | Consume | False | 2381.5 | 2402.5 | 0.9912591050988554 | 0.99167335294642067 | True |
| finite-large-mask1-oneway | Query | True | 282027 | 272848.5 | 1.0336395472212601 | 1.0238596694831767 | False |
| finite-large-mask1-oneway | Consume | False | 699 | 697 | 1.0028694404591105 | 1.0773078936652902 | False |
| finite-large-mask1-mutual | Query | True | 325859 | 281441 | 1.1578234869830621 | 1.1378207371981459 | False |
| finite-large-mask1-mutual | Consume | False | 34 | 31 | 1.096774193548387 | 1.2881578947368404 | False |
| finite-large-mask4-oneway | Query | True | 288436 | 290680.5 | 0.99227846381164198 | 1.0135562196978161 | False |
| finite-large-mask4-oneway | Consume | False | 2549 | 2584.5 | 0.98626426775004838 | 1.0040794862397016 | False |
| finite-large-mask4-mutual | Query | True | 289364.5 | 290659 | 0.99554632748340843 | 0.9921062944793575 | True |
| finite-large-mask4-mutual | Consume | False | 1272.5 | 1243 | 1.0237329042638776 | 1.0528786920182476 | False |
| finite-large-mask8-oneway | Query | True | 267932.5 | 267718 | 1.0008012162051114 | 1.0185029409910049 | False |
| finite-large-mask8-oneway | Consume | False | 4917.5 | 4922 | 0.99908573750507923 | 1.0196937866881066 | False |
| finite-large-mask8-mutual | Query | True | 269725.5 | 269662 | 1.0002354799712232 | 1.0172693441360978 | False |
| finite-large-mask8-mutual | Consume | False | 4954 | 4886.5 | 1.0138135679934512 | 1.0685172624005073 | False |
| default-large-dynamic10 | Query | True | 273752.5 | 268635.5 | 1.0190481153831121 | 1.0790591480947558 | False |
| default-large-dynamic10 | Consume | False | 9348 | 9346 | 1.0002139952921036 | 1.0263847530949628 | False |
| default-large-dynamic50 | Query | True | 298599 | 289961.5 | 1.0297884374304864 | 1.0492299976478161 | False |
| default-large-dynamic50 | Consume | False | 36211 | 36265 | 0.99851096098166281 | 1.0085274397216906 | False |
| default-large-dynamic90 | Query | True | 307787 | 299360 | 1.0281500534473544 | 1.0516697701187403 | False |
| default-large-dynamic90 | Consume | False | 47640 | 47690.5 | 0.99894108889611144 | 0.99633793965177464 | True |

Required metrics: 6/27 pass. Case reports: 27.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
