# optimized-20 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4019 | 3974.5 | 1.011196376902755 | 1.0112515395967645 | False |
| finite-small-mask1-oneway | Consume | False | 9 | 9 | 1 | 0.91666666666666663 | True |
| finite-small-mask1-mutual | Query | True | 3991 | 4017.5 | 0.99340385812072185 | 1.0104490225993041 | False |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1 | True |
| finite-small-mask4-oneway | Query | True | 4262.5 | 4122 | 1.0340853954391072 | 1.0370206425980308 | False |
| finite-small-mask4-oneway | Consume | False | 30 | 30 | 1 | 1.0485294117647053 | False |
| finite-small-mask4-mutual | Query | True | 4439 | 4435.5 | 1.0007890880396799 | 0.99436979061759601 | False |
| finite-small-mask4-mutual | Consume | False | 16 | 17 | 0.94117647058823528 | 0.95454545454545459 | True |
| finite-small-mask8-oneway | Query | True | 4248.5 | 4018.5 | 1.0572352867985566 | 1.0613704027536441 | False |
| finite-small-mask8-oneway | Consume | False | 59 | 58 | 1.0172413793103448 | 1.0098484848484846 | False |
| finite-small-mask8-mutual | Query | True | 4039 | 4281.5 | 0.94336097162209509 | 0.96680661789158651 | True |
| finite-small-mask8-mutual | Consume | False | 59 | 59 | 1 | 1.0615384615384615 | False |
| default-small-dynamic10 | Query | True | 1110.5 | 1133.5 | 0.97970886634318488 | 0.95228564280353123 | True |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1 | True |
| default-small-dynamic50 | Query | True | 1304 | 1328.5 | 0.98155814828754229 | 1.026719592392914 | False |
| default-small-dynamic50 | Consume | False | 109 | 108 | 1.0092592592592593 | 0.51769911504424782 | False |
| default-small-dynamic90 | Query | True | 1308 | 1304 | 1.0030674846625767 | 0.96410583166454256 | False |
| default-small-dynamic90 | Consume | False | 141 | 140 | 1.0071428571428571 | 0.87485515643105494 | False |
| character-small-hair16-clustered | Query | True | 4368 | 4487 | 0.97347893915756634 | 0.7099735737379117 | True |
| character-small-hair16-clustered | Consume | False | 121 | 122 | 0.99180327868852458 | 0.70798538622129459 | True |
| finite-middle-mask1-oneway | Query | True | 32403.5 | 31860.5 | 1.0170430470331602 | 1.0015573097221036 | False |
| finite-middle-mask1-oneway | Consume | False | 69.5 | 69 | 1.0072463768115942 | 0.99581589958158967 | False |
| finite-middle-mask1-mutual | Query | True | 31949 | 32191 | 0.99248237084899504 | 0.99382388272776345 | True |
| finite-middle-mask1-mutual | Consume | False | 5 | 6 | 0.83333333333333337 | 1.2222222222222223 | False |
| finite-middle-mask4-oneway | Query | True | 34627.5 | 33265 | 1.0409589658800542 | 1.0340852620117815 | False |
| finite-middle-mask4-oneway | Consume | False | 243 | 245 | 0.99183673469387756 | 0.98499142367066927 | True |
| finite-middle-mask4-mutual | Query | True | 34347.5 | 34088.5 | 1.0075978702494977 | 1.0079290816409798 | False |
| finite-middle-mask4-mutual | Consume | False | 128 | 128 | 1 | 0.98968427633635525 | True |
| finite-middle-mask8-oneway | Query | True | 32556 | 31122.5 | 1.0460599244919271 | 1.0512717179172535 | False |
| finite-middle-mask8-oneway | Consume | False | 462 | 463 | 0.99784017278617709 | 1.0756876815308387 | False |
| finite-middle-mask8-mutual | Query | True | 31588 | 31544 | 1.0013948769972103 | 0.76182800796435035 | False |
| finite-middle-mask8-mutual | Consume | False | 460 | 461 | 0.99783080260303691 | 0.97158908507223107 | True |
| default-middle-dynamic10 | Query | True | 15960.5 | 16279.5 | 0.98040480358733373 | 0.98890439917738016 | True |
| default-middle-dynamic10 | Consume | False | 459 | 459 | 1 | 0.98150016818028929 | True |
| default-middle-dynamic50 | Query | True | 17385.5 | 17669 | 0.98395494934631278 | 0.9713345142892249 | True |
| default-middle-dynamic50 | Consume | False | 1745 | 1746 | 0.99942726231386025 | 0.96097838183411077 | True |
| default-middle-dynamic90 | Query | True | 17854 | 18099.5 | 0.98643608939473471 | 0.98101313012111979 | True |
| default-middle-dynamic90 | Consume | False | 2300 | 2300 | 1 | 1.0213126182651129 | False |
| character-middle-hair64-interleaved | Query | True | 58588.5 | 63540.5 | 0.9220654543165383 | 0.92064318059484074 | True |
| character-middle-hair64-interleaved | Consume | False | 2012.5 | 2045 | 0.9841075794621027 | 0.98290121159492172 | True |
| finite-large-mask1-oneway | Query | True | 266380.5 | 263642 | 1.0103871917221081 | 1.0127405484271161 | False |
| finite-large-mask1-oneway | Consume | False | 632 | 611.5 | 1.0335241210139003 | 1.0228800413650472 | False |
| finite-large-mask1-mutual | Query | True | 268422 | 262132 | 1.0239955442296247 | 1.0145741153413304 | False |
| finite-large-mask1-mutual | Consume | False | 29 | 28 | 1.0357142857142858 | 1.088235294117647 | False |
| finite-large-mask4-oneway | Query | True | 278029 | 274959 | 1.011165301008514 | 1.0319561436122862 | False |
| finite-large-mask4-oneway | Consume | False | 2203 | 2162 | 1.018963922294172 | 1.0079775364474859 | False |
| finite-large-mask4-mutual | Query | True | 286836 | 282727 | 1.0145334545338789 | 1.0121416750592616 | False |
| finite-large-mask4-mutual | Consume | False | 1141.5 | 1173.5 | 0.97273114614401368 | 1.0684485006518902 | False |
| finite-large-mask8-oneway | Query | True | 264264 | 268480 | 0.98429678188319425 | 0.98222641617463646 | True |
| finite-large-mask8-oneway | Consume | False | 4340.5 | 4366 | 0.99415941365093907 | 0.96837703873951209 | True |
| finite-large-mask8-mutual | Query | True | 273951.5 | 265095 | 1.0334087779852505 | 1.0233693762790348 | False |
| finite-large-mask8-mutual | Consume | False | 4393.5 | 4387.5 | 1.0013675213675213 | 0.96764339327379911 | False |
| default-large-dynamic10 | Query | True | 247647 | 252052 | 0.98252344754257059 | 0.98229349085684714 | True |
| default-large-dynamic10 | Consume | False | 7984 | 8136 | 0.98131760078662733 | 1.0144083286125036 | False |
| default-large-dynamic50 | Query | True | 274297 | 279200 | 0.98243911174785103 | 0.98437023262524004 | True |
| default-large-dynamic50 | Consume | False | 32761.5 | 33010 | 0.99247197818842769 | 0.99084105199296568 | True |
| default-large-dynamic90 | Query | True | 285614 | 290829 | 0.98206850073410834 | 0.99269611649701595 | True |
| default-large-dynamic90 | Consume | False | 44630.5 | 44433.5 | 1.0044335917719738 | 1.0057683130393464 | False |
| character-large-hair64-clustered | Query | True | 980783 | 1031577.5 | 0.95076036458724622 | 0.95312399825006466 | True |
| character-large-hair64-clustered | Consume | False | 43250 | 43354.5 | 0.99758963890714925 | 0.99461620845117382 | True |

Required metrics: 13/30 pass. Case reports: 30.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
