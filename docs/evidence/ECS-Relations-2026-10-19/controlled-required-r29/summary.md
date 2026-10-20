# controlled-required-r29 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4074.5 | 4077.5 | 0.99926425505824645 | 1.0110236930498884 | False |
| finite-small-mask1-oneway | Consume | False | 10 | 9 | 1.1111111111111112 | 1.0909090909090908 | False |
| finite-small-mask1-mutual | Query | True | 4078 | 4061.5 | 1.0040625384710082 | 0.98118553902942163 | False |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1 | True |
| finite-small-mask4-oneway | Query | True | 4240 | 4530 | 0.9359823399558499 | 0.93871143740487961 | True |
| finite-small-mask4-oneway | Consume | False | 30 | 30 | 1 | 1.0285714285714285 | False |
| finite-small-mask4-mutual | Query | True | 4295 | 4299 | 0.99906955105838569 | 0.97304128053917449 | True |
| finite-small-mask4-mutual | Consume | False | 16 | 16 | 1 | 1.0342105263157884 | False |
| finite-small-mask8-oneway | Query | True | 4266.5 | 4278.5 | 0.99719527871917724 | 0.98556427791041146 | True |
| finite-small-mask8-oneway | Consume | False | 61 | 60 | 1.0166666666666666 | 1.0298507462686568 | False |
| finite-small-mask8-mutual | Query | True | 4134.5 | 4130.5 | 1.0009684057620143 | 0.95960915402417102 | False |
| finite-small-mask8-mutual | Consume | False | 59 | 59 | 1 | 0.99039172209903947 | True |
| default-small-dynamic10 | Query | True | 1121 | 1123 | 0.99821905609973283 | 1.0042381936035327 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1 | True |
| default-small-dynamic50 | Query | True | 1241.5 | 1241 | 1.000402900886382 | 1.0270469798657715 | False |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 1.0058035714285711 | False |
| default-small-dynamic90 | Query | True | 1395.5 | 1372 | 1.0171282798833818 | 1.0198570776949976 | False |
| default-small-dynamic90 | Consume | False | 143.5 | 143 | 1.0034965034965035 | 1.6993464052287581 | False |
| finite-middle-mask1-oneway | Query | True | 34040.5 | 33417 | 1.0186581679983242 | 0.98422181516027718 | False |
| finite-middle-mask1-oneway | Consume | False | 69 | 68.5 | 1.0072992700729928 | 0.94899169632265767 | False |
| finite-middle-mask1-mutual | Query | True | 32843 | 33191 | 0.98951523003223762 | 0.98233669392147061 | True |
| finite-middle-mask1-mutual | Consume | False | 6 | 6 | 1 | 1.1000000000000001 | False |
| finite-middle-mask4-oneway | Query | True | 33863 | 33386.5 | 1.0142722357839247 | 1.0120652696108041 | False |
| finite-middle-mask4-oneway | Consume | False | 243 | 242 | 1.0041322314049588 | 1.3376499714340127 | False |
| finite-middle-mask4-mutual | Query | True | 33772.5 | 33949 | 0.99480102506701229 | 0.9962916804717814 | True |
| finite-middle-mask4-mutual | Consume | False | 127 | 126 | 1.0079365079365079 | 0.99748201438848905 | False |
| finite-middle-mask8-oneway | Query | True | 33854.5 | 33135.5 | 1.021698782272789 | 0.99663694601888253 | False |
| finite-middle-mask8-oneway | Consume | False | 466 | 464 | 1.0043103448275863 | 0.99843311891802677 | False |
| finite-middle-mask8-mutual | Query | True | 31699 | 32002 | 0.99053184175988995 | 0.98593818304297676 | True |
| finite-middle-mask8-mutual | Consume | False | 464 | 464 | 1 | 0.97293343087051942 | True |
| default-middle-dynamic10 | Query | True | 15934.5 | 16016.5 | 0.99488027971154747 | 0.99120025232251396 | True |
| default-middle-dynamic10 | Consume | False | 460 | 459 | 1.0021786492374727 | 0.99881154499151092 | False |
| default-middle-dynamic50 | Query | True | 17798 | 17691.5 | 1.0060198400361755 | 1.0042965901695897 | False |
| default-middle-dynamic50 | Consume | False | 1799.5 | 1798.5 | 1.0005560189046427 | 1.0258770239013106 | False |
| default-middle-dynamic90 | Query | True | 19347 | 19175 | 1.0089700130378096 | 0.99856884467555962 | False |
| default-middle-dynamic90 | Consume | False | 2503 | 2497 | 1.0024028834601522 | 1.0127545350407767 | False |
| finite-large-mask1-oneway | Query | True | 268294 | 268282 | 1.0000447290537569 | 0.99143713387687027 | False |
| finite-large-mask1-oneway | Consume | False | 682.5 | 682 | 1.000733137829912 | 1.0446226250653652 | False |
| finite-large-mask1-mutual | Query | True | 268270.5 | 270416.5 | 0.9920640937220917 | 0.9835128506211317 | True |
| finite-large-mask1-mutual | Consume | False | 34 | 32 | 1.0625 | 1.0769230769230769 | False |
| finite-large-mask4-oneway | Query | True | 280246 | 296116.5 | 0.94640454010499242 | 0.94651994909557768 | True |
| finite-large-mask4-oneway | Consume | False | 2401.5 | 2408 | 0.99730066445182719 | 0.98777279058969181 | True |
| finite-large-mask4-mutual | Query | True | 299305 | 299425.5 | 0.99959756266583843 | 1.0418684240333498 | False |
| finite-large-mask4-mutual | Consume | False | 1331.5 | 1353 | 0.98410938654841096 | 0.96884726140958133 | True |
| finite-large-mask8-oneway | Query | True | 277129.5 | 269241.5 | 1.0292971180148678 | 1.0234865890716875 | False |
| finite-large-mask8-oneway | Consume | False | 4891 | 4976 | 0.98291800643086813 | 0.92886147809235886 | True |
| finite-large-mask8-mutual | Query | True | 292853.5 | 286250.5 | 1.0230672086162296 | 1.0247774812283339 | False |
| finite-large-mask8-mutual | Consume | False | 5100.5 | 5107 | 0.99872723712551403 | 1.0210266622592374 | False |
| default-large-dynamic10 | Query | True | 252672.5 | 255643 | 0.98838028031277991 | 0.99942003638907906 | True |
| default-large-dynamic10 | Consume | False | 8644.5 | 8673 | 0.99671393981321343 | 0.99578080341165143 | True |
| default-large-dynamic50 | Query | True | 282335 | 282275 | 1.000212558675051 | 1.0059990403673869 | False |
| default-large-dynamic50 | Consume | False | 34662.5 | 34971 | 0.99117840496411314 | 0.99141239963143357 | True |
| default-large-dynamic90 | Query | True | 293853.5 | 292516 | 1.0045723994584912 | 0.99664717413164072 | False |
| default-large-dynamic90 | Consume | False | 46386 | 46586.5 | 0.99569617807734001 | 0.99902139965758974 | True |

Required metrics: 10/27 pass. Case reports: 27.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
