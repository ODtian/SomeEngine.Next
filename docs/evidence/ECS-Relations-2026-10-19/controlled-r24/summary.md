# controlled-r24 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4287.5 | 4238.5 | 1.0115606936416186 | 0.99945997717639434 | False |
| finite-small-mask1-oneway | Consume | False | 10 | 11 | 0.90909090909090906 | 1 | True |
| finite-small-mask1-mutual | Query | True | 4266 | 4305.5 | 0.99082568807339455 | 0.97987180255196804 | True |
| finite-small-mask1-mutual | Consume | False | 3 | 2 | 1.5 | 1 | False |
| finite-small-mask4-oneway | Query | True | 4521 | 4621 | 0.97835966241073358 | 0.96036129828315853 | True |
| finite-small-mask4-oneway | Consume | False | 32 | 32 | 1 | 0.83242059145673641 | True |
| finite-small-mask4-mutual | Query | True | 4650.5 | 4648.5 | 1.000430246316016 | 1.01588237606636 | False |
| finite-small-mask4-mutual | Consume | False | 18 | 18 | 1 | 1.03095238095238 | False |
| finite-small-mask8-oneway | Query | True | 4316 | 4409.5 | 0.97879578183467508 | 0.93726134222643132 | True |
| finite-small-mask8-oneway | Consume | False | 63 | 63.5 | 0.99212598425196852 | 1.0209102091020916 | False |
| finite-small-mask8-mutual | Query | True | 4295.5 | 4272.5 | 1.0053832650672909 | 0.9070572442228404 | False |
| finite-small-mask8-mutual | Consume | False | 61 | 74 | 0.82432432432432434 | 0.74426719840478572 | True |
| default-small-dynamic10 | Query | True | 1122 | 1114 | 1.0071813285457809 | 1.0089935290461742 | False |
| default-small-dynamic10 | Consume | False | 29 | 28 | 1.0357142857142858 | 1.0333333333333334 | False |
| default-small-dynamic50 | Query | True | 1243 | 1225 | 1.0146938775510204 | 1.0448454292158686 | False |
| default-small-dynamic50 | Consume | False | 106 | 106 | 1 | 1.032300884955752 | False |
| default-small-dynamic90 | Query | True | 1278.5 | 1262.5 | 1.0126732673267327 | 0.97766681797548816 | False |
| default-small-dynamic90 | Consume | False | 140 | 139 | 1.0071942446043165 | 1.1077119184193718 | False |
| finite-middle-mask1-oneway | Query | True | 31203 | 31228.5 | 0.99918343820548539 | 0.99616188718156873 | True |
| finite-middle-mask1-oneway | Consume | False | 67 | 68 | 0.98529411764705888 | 0.94417767106842765 | True |
| finite-middle-mask1-mutual | Query | True | 31810 | 31150.5 | 1.0211714097687035 | 1.0230957723327747 | False |
| finite-middle-mask1-mutual | Consume | False | 6 | 5 | 1.2 | 1.1111111111111112 | False |
| finite-middle-mask4-oneway | Query | True | 32918.5 | 33143 | 0.99322632230033492 | 0.98674980733245654 | True |
| finite-middle-mask4-oneway | Consume | False | 241 | 238 | 1.0126050420168067 | 1.1544839255499124 | False |
| finite-middle-mask4-mutual | Query | True | 33709.5 | 33344.5 | 1.0109463329784523 | 1.0086504605979749 | False |
| finite-middle-mask4-mutual | Consume | False | 127 | 127 | 1 | 1.0175675675675671 | False |
| finite-middle-mask8-oneway | Query | True | 32482 | 31285.5 | 1.0382445541864442 | 1.0338647417528839 | False |
| finite-middle-mask8-oneway | Consume | False | 461 | 462 | 0.99783549783549785 | 0.93050688092999401 | True |
| finite-middle-mask8-mutual | Query | True | 31790.5 | 31047.5 | 1.0239310733553426 | 1.0175478547008676 | False |
| finite-middle-mask8-mutual | Consume | False | 459 | 455 | 1.0087912087912088 | 0.97269503546099279 | False |
| default-middle-dynamic10 | Query | True | 16031.5 | 16022 | 1.0005929347147673 | 0.99697798820357564 | False |
| default-middle-dynamic10 | Consume | False | 464 | 463 | 1.0021598272138228 | 0.99295832988153443 | False |
| default-middle-dynamic50 | Query | True | 17243.5 | 17052 | 1.0112303542106498 | 1.0010711728942503 | False |
| default-middle-dynamic50 | Consume | False | 1744 | 1745 | 0.99942693409742123 | 0.96513739256748599 | True |
| default-middle-dynamic90 | Query | True | 18018.5 | 17683 | 1.0189730249392071 | 1.0560978102228034 | False |
| default-middle-dynamic90 | Consume | False | 2304 | 2297 | 1.0030474531998259 | 1.0245212517515181 | False |
| finite-large-mask1-oneway | Query | True | 268614 | 261854 | 1.0258159126841675 | 1.0223981355895675 | False |
| finite-large-mask1-oneway | Consume | False | 641.5 | 646.5 | 0.99226604795050266 | 1.0674854314180084 | False |
| finite-large-mask1-mutual | Query | True | 263359.5 | 262697 | 1.0025219168852328 | 0.99641920195472511 | False |
| finite-large-mask1-mutual | Consume | False | 30 | 27 | 1.1111111111111112 | 1.0779411764705875 | False |
| finite-large-mask4-oneway | Query | True | 284802.5 | 275007.5 | 1.0356172104397152 | 1.0369213984481769 | False |
| finite-large-mask4-oneway | Consume | False | 2245.5 | 2208.5 | 1.0167534525696174 | 1.0215199143710212 | False |
| finite-large-mask4-mutual | Query | True | 283217 | 278774.5 | 1.0159358190939272 | 1.0298063925142433 | False |
| finite-large-mask4-mutual | Consume | False | 1174 | 1150.5 | 1.020425901781834 | 0.988914347327987 | False |
| finite-large-mask8-oneway | Query | True | 261626.5 | 258875.5 | 1.0106267298373157 | 1.0237759868608811 | False |
| finite-large-mask8-oneway | Consume | False | 4457 | 4486.5 | 0.99342471860024517 | 1.0483588354900066 | False |
| finite-large-mask8-mutual | Query | True | 263152.5 | 260182.5 | 1.0114150644259319 | 1.0164514358860133 | False |
| finite-large-mask8-mutual | Consume | False | 4341 | 4365.5 | 0.99438781353796812 | 0.97826209171148537 | True |
| default-large-dynamic10 | Query | True | 246134 | 246664 | 0.99785132812246624 | 0.99842442741386506 | True |
| default-large-dynamic10 | Consume | False | 7957.5 | 8050 | 0.98850931677018639 | 0.99557283942479879 | True |
| default-large-dynamic50 | Query | True | 272666.5 | 272081 | 1.0021519326965131 | 0.99989612269048656 | False |
| default-large-dynamic50 | Consume | False | 32686 | 33255 | 0.98288979100887086 | 0.98868461403389918 | True |
| default-large-dynamic90 | Query | True | 282895.5 | 282059 | 1.0029656915751668 | 1.0004740342473042 | False |
| default-large-dynamic90 | Consume | False | 44241.5 | 44309 | 0.99847660746123812 | 0.99777610553619389 | True |

Required metrics: 6/27 pass. Case reports: 27.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
