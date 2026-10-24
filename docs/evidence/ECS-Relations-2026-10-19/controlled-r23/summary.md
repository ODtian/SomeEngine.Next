# controlled-r23 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4233.5 | 4364.5 | 0.96998510711421693 | 0.7832853544440449 | True |
| finite-small-mask1-oneway | Consume | False | 11 | 12 | 0.91666666666666663 | 0.9285714285714286 | True |
| finite-small-mask1-mutual | Query | True | 4017 | 4058.5 | 0.9897745472465197 | 0.96950305735131659 | True |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1 | True |
| finite-small-mask4-oneway | Query | True | 4430.5 | 4255 | 1.0412455934195064 | 0.94992570720119607 | False |
| finite-small-mask4-oneway | Consume | False | 30 | 31 | 0.967741935483871 | 0.94444444444444442 | True |
| finite-small-mask4-mutual | Query | True | 4160.5 | 4250 | 0.9789411764705882 | 0.92956853430489439 | True |
| finite-small-mask4-mutual | Consume | False | 16 | 16 | 1 | 0.94736842105263153 | True |
| finite-small-mask8-oneway | Query | True | 4277.5 | 4102.5 | 1.0426569165143205 | 0.98657970258283345 | False |
| finite-small-mask8-oneway | Consume | False | 60 | 58.5 | 1.0256410256410255 | 0.99024756189047292 | False |
| finite-small-mask8-mutual | Query | True | 4097.5 | 4194.5 | 0.97687447848372866 | 0.9416599190283399 | True |
| finite-small-mask8-mutual | Consume | False | 61 | 61 | 1 | 0.98550724637681164 | True |
| default-small-dynamic10 | Query | True | 1112 | 1111.5 | 1.0004498425551056 | 0.98329302325581425 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1 | True |
| default-small-dynamic50 | Query | True | 1243 | 1224 | 1.0155228758169934 | 1.0575146208949024 | False |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 1.0031631269769545 | False |
| default-small-dynamic90 | Query | True | 1287 | 1273 | 1.0109976433621366 | 1.071020339552867 | False |
| default-small-dynamic90 | Consume | False | 139 | 139 | 1 | 1.225632911392402 | False |
| finite-middle-mask1-oneway | Query | True | 31619 | 31304.5 | 1.0100464789407273 | 1.0092861690263368 | False |
| finite-middle-mask1-oneway | Consume | False | 67 | 67 | 1 | 1.0589041095890406 | False |
| finite-middle-mask1-mutual | Query | True | 31539.5 | 31416.5 | 1.0039151401333695 | 1.0162399754868296 | False |
| finite-middle-mask1-mutual | Consume | False | 6 | 6 | 1 | 1 | True |
| finite-middle-mask4-oneway | Query | True | 33026.5 | 32954.5 | 1.0021848305997663 | 1.0081208829176838 | False |
| finite-middle-mask4-oneway | Consume | False | 240 | 239 | 1.00418410041841 | 0.82273073989321321 | False |
| finite-middle-mask4-mutual | Query | True | 34289 | 33619.5 | 1.0199140379839082 | 1.0257755884603643 | False |
| finite-middle-mask4-mutual | Consume | False | 126 | 126 | 1 | 0.94276315789473653 | True |
| finite-middle-mask8-oneway | Query | True | 32703 | 32066.5 | 1.0198493755165048 | 0.99577235953615773 | False |
| finite-middle-mask8-oneway | Consume | False | 466 | 464 | 1.0043103448275863 | 1.0626681436936223 | False |
| finite-middle-mask8-mutual | Query | True | 31590.5 | 32746.5 | 0.96469851739880597 | 0.94888267644492197 | True |
| finite-middle-mask8-mutual | Consume | False | 461.5 | 459 | 1.0054466230936818 | 1.0084224711530363 | False |
| default-middle-dynamic10 | Query | True | 16046.5 | 15952 | 1.0059240220661987 | 1.0180807831853587 | False |
| default-middle-dynamic10 | Consume | False | 460 | 459 | 1.0021786492374727 | 0.9966867725766716 | False |
| default-middle-dynamic50 | Query | True | 17311.5 | 17078.5 | 1.013642884328249 | 1.0276081583203076 | False |
| default-middle-dynamic50 | Consume | False | 1744 | 1744 | 1 | 1.034301058924862 | False |
| default-middle-dynamic90 | Query | True | 17807.5 | 17753 | 1.0030699036782516 | 0.98971435874496771 | False |
| default-middle-dynamic90 | Consume | False | 2298 | 2296 | 1.0008710801393728 | 0.94190322106192115 | False |
| finite-large-mask1-oneway | Query | True | 266953 | 260761.5 | 1.0237439192518833 | 1.0112934602973018 | False |
| finite-large-mask1-oneway | Consume | False | 601 | 595.5 | 1.0092359361880772 | 0.97670760481577845 | False |
| finite-large-mask1-mutual | Query | True | 266176 | 274796 | 0.96863127556441875 | 0.96319032843244878 | True |
| finite-large-mask1-mutual | Consume | False | 28 | 26 | 1.0769230769230769 | 1.0606060606060606 | False |
| finite-large-mask4-oneway | Query | True | 280757.5 | 276025.5 | 1.0171433436403521 | 1.0196243405767225 | False |
| finite-large-mask4-oneway | Consume | False | 2212 | 2236.5 | 0.98904538341158055 | 1.0108902804247208 | False |
| finite-large-mask4-mutual | Query | True | 298342.5 | 284031 | 1.0503871056328358 | 1.0518545602965936 | False |
| finite-large-mask4-mutual | Consume | False | 1104.5 | 1109.5 | 0.99549346552501128 | 1.0135491692219925 | False |
| finite-large-mask8-oneway | Query | True | 271465 | 264902 | 1.0247751998852406 | 1.0040036102781102 | False |
| finite-large-mask8-oneway | Consume | False | 4316.5 | 4374 | 0.98685413808870603 | 0.98534169878738431 | True |
| finite-large-mask8-mutual | Query | True | 265863 | 265271.5 | 1.0022297909877238 | 1.006544724316317 | False |
| finite-large-mask8-mutual | Consume | False | 4751.5 | 4686.5 | 1.013869625520111 | 1.0259470914804789 | False |
| default-large-dynamic10 | Query | True | 246349.5 | 246107.5 | 1.0009833101388621 | 0.99903282438019603 | False |
| default-large-dynamic10 | Consume | False | 8140.5 | 8030 | 1.013760896637609 | 1.0319743073322085 | False |
| default-large-dynamic50 | Query | True | 274157 | 271052 | 1.0114553664979413 | 1.0087402649780919 | False |
| default-large-dynamic50 | Consume | False | 33128.5 | 33085.5 | 1.0012996629943631 | 1.0086153975234751 | False |
| default-large-dynamic90 | Query | True | 282689.5 | 281239 | 1.0051575350502597 | 1.0072924356298218 | False |
| default-large-dynamic90 | Consume | False | 44058.5 | 43808.5 | 1.0057066551011791 | 1.0030361738426392 | False |

Required metrics: 6/27 pass. Case reports: 27.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
