# controlled-fixed-r26 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 3997 | 4071 | 0.98182264799803487 | 0.99729971037216092 | True |
| finite-small-mask1-oneway | Consume | False | 10 | 9 | 1.1111111111111112 | 1.0833333333333333 | False |
| finite-small-mask1-mutual | Query | True | 4139.5 | 4124 | 1.0037584869059166 | 1.0038244745298419 | False |
| finite-small-mask1-mutual | Consume | False | 3 | 2 | 1.5 | 1.25 | False |
| finite-small-mask4-oneway | Query | True | 4387.5 | 4462.5 | 0.98319327731092432 | 0.95444860631415096 | True |
| finite-small-mask4-oneway | Consume | False | 32 | 32 | 1 | 0.95617529880478147 | True |
| finite-small-mask4-mutual | Query | True | 4420 | 4351 | 1.0158584233509538 | 1.1196667671244673 | False |
| finite-small-mask4-mutual | Consume | False | 16 | 16 | 1 | 1.0342105263157884 | False |
| finite-small-mask8-oneway | Query | True | 4354.5 | 4295.5 | 1.0137353043883133 | 1.0461445011080555 | False |
| finite-small-mask8-oneway | Consume | False | 62 | 62 | 1 | 1.0769230769230769 | False |
| finite-small-mask8-mutual | Query | True | 4381.5 | 4325 | 1.0130635838150288 | 0.95734513964737089 | False |
| finite-small-mask8-mutual | Consume | False | 63 | 61 | 1.0327868852459017 | 0.96009174311926582 | False |
| default-small-dynamic10 | Query | True | 1238 | 1211.5 | 1.0218737102765167 | 1.0562243438955801 | False |
| default-small-dynamic10 | Consume | False | 30 | 29 | 1.0344827586206897 | 1 | False |
| default-small-dynamic50 | Query | True | 1445.5 | 1402 | 1.0310271041369472 | 0.97523734027895759 | False |
| default-small-dynamic50 | Consume | False | 112 | 112 | 1 | 1.0080524903071877 | False |
| default-small-dynamic90 | Query | True | 1376.5 | 1339 | 1.0280059746079164 | 1.1055940895604501 | False |
| default-small-dynamic90 | Consume | False | 148 | 144 | 1.0277777777777777 | 0.98209428830462409 | False |
| finite-middle-mask1-oneway | Query | True | 32578.5 | 32636.5 | 0.99822284865105015 | 1.1083656545340357 | False |
| finite-middle-mask1-oneway | Consume | False | 70 | 69 | 1.0144927536231885 | 1.2049450549450547 | False |
| finite-middle-mask1-mutual | Query | True | 31806.5 | 31468.5 | 1.0107408996297884 | 1.0224291004797026 | False |
| finite-middle-mask1-mutual | Consume | False | 5 | 5 | 1 | 1.25 | False |
| finite-middle-mask4-oneway | Query | True | 33088 | 33293.5 | 0.9938276240106928 | 0.99420282960125039 | True |
| finite-middle-mask4-oneway | Consume | False | 244 | 243 | 1.0041152263374487 | 0.79057517084282491 | False |
| finite-middle-mask4-mutual | Query | True | 34370.5 | 35560 | 0.96654949381327337 | 0.9531935978094338 | True |
| finite-middle-mask4-mutual | Consume | False | 126 | 127 | 0.99212598425196852 | 0.95912361020274761 | True |
| finite-middle-mask8-oneway | Query | True | 31797.5 | 31328 | 1.0149865934627171 | 1.0178980034619305 | False |
| finite-middle-mask8-oneway | Consume | False | 462 | 463 | 0.99784017278617709 | 0.91811816939890722 | True |
| finite-middle-mask8-mutual | Query | True | 31525 | 31974.5 | 0.98594192246946788 | 0.98591765247030794 | True |
| finite-middle-mask8-mutual | Consume | False | 461 | 462 | 0.99783549783549785 | 0.93043103448275855 | True |
| default-middle-dynamic10 | Query | True | 15970 | 15766.5 | 1.0129071131830145 | 0.99810109360798005 | False |
| default-middle-dynamic10 | Consume | False | 459 | 459 | 1 | 1.0062173364570188 | False |
| default-middle-dynamic50 | Query | True | 17617 | 17110 | 1.0296317942723554 | 1.0243374911868706 | False |
| default-middle-dynamic50 | Consume | False | 1751 | 1743 | 1.0045897877223178 | 1.0417153896995268 | False |
| default-middle-dynamic90 | Query | True | 18313.5 | 17705 | 1.034368822366563 | 1.0352086961775591 | False |
| default-middle-dynamic90 | Consume | False | 2299 | 2300 | 0.99956521739130433 | 1.0167259317792703 | False |
| finite-large-mask1-oneway | Query | True | 267485 | 260716 | 1.0259631169548473 | 1.0523596916885978 | False |
| finite-large-mask1-oneway | Consume | False | 630 | 608 | 1.0361842105263157 | 1.1319199840499772 | False |
| finite-large-mask1-mutual | Query | True | 263820 | 260929 | 1.0110796423548167 | 1.0046170686055602 | False |
| finite-large-mask1-mutual | Consume | False | 29 | 27 | 1.0740740740740742 | 1 | False |
| finite-large-mask4-oneway | Query | True | 276485.5 | 277464.5 | 0.99647162069381845 | 1.0010038314669423 | False |
| finite-large-mask4-oneway | Consume | False | 2298.5 | 2304 | 0.99761284722222221 | 1.0335659506415149 | False |
| finite-large-mask4-mutual | Query | True | 286909 | 295570.5 | 0.97069565467460384 | 0.96952927828845303 | True |
| finite-large-mask4-mutual | Consume | False | 1243 | 1247 | 0.99679230152365672 | 0.93141525059812302 | True |
| finite-large-mask8-oneway | Query | True | 278793 | 275547 | 1.0117802044660258 | 0.9587480311049823 | False |
| finite-large-mask8-oneway | Consume | False | 5298 | 5317.5 | 0.99633286318758818 | 0.9894439807638804 | True |
| finite-large-mask8-mutual | Query | True | 274651 | 269431 | 1.0193741625870816 | 1.0075540454933403 | False |
| finite-large-mask8-mutual | Consume | False | 4952 | 4865.5 | 1.0177782345082724 | 1.0190189926261854 | False |
| default-large-dynamic10 | Query | True | 268190.5 | 268334 | 0.99946521871995353 | 0.98140307605611876 | True |
| default-large-dynamic10 | Consume | False | 9032 | 9066.5 | 0.99619478299233444 | 1.0089669402951602 | False |
| default-large-dynamic50 | Query | True | 282929.5 | 274600.5 | 1.0303313358861328 | 1.0566557359095816 | False |
| default-large-dynamic50 | Consume | False | 34282 | 33990.5 | 1.0085759256262778 | 1.0075875496859952 | False |
| default-large-dynamic90 | Query | True | 293433 | 284503 | 1.0313880697215847 | 1.048534313144172 | False |
| default-large-dynamic90 | Consume | False | 45671 | 44968.5 | 1.0156220465436916 | 1.0224834883995422 | False |

Required metrics: 7/27 pass. Case reports: 27.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
