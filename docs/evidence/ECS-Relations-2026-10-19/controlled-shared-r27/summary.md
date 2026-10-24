# controlled-shared-r27 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4017.5 | 4257.5 | 0.94362889019377572 | 0.94081924646282655 | True |
| finite-small-mask1-oneway | Consume | False | 10 | 9 | 1.1111111111111112 | 1.1818181818181819 | False |
| finite-small-mask1-mutual | Query | True | 4046 | 4276.5 | 0.946100783350871 | 0.93935698214773855 | True |
| finite-small-mask1-mutual | Consume | False | 3 | 2 | 1.5 | 1.25 | False |
| finite-small-mask4-oneway | Query | True | 4238.5 | 4256 | 0.99588815789473684 | 1.080183603101047 | False |
| finite-small-mask4-oneway | Consume | False | 32 | 31 | 1.032258064516129 | 1.088235294117647 | False |
| finite-small-mask4-mutual | Query | True | 4285 | 4308 | 0.994661095636026 | 1.0598204958748161 | False |
| finite-small-mask4-mutual | Consume | False | 17 | 16 | 1.0625 | 1.2222222222222223 | False |
| finite-small-mask8-oneway | Query | True | 4061 | 4070 | 0.99778869778869783 | 0.97157457334736519 | True |
| finite-small-mask8-oneway | Consume | False | 59 | 58 | 1.0172413793103448 | 1.0054137664346485 | False |
| finite-small-mask8-mutual | Query | True | 4136.5 | 4181 | 0.98935661325041857 | 0.99920597273495981 | True |
| finite-small-mask8-mutual | Consume | False | 60 | 58 | 1.0344827586206897 | 1.0303030303030303 | False |
| default-small-dynamic10 | Query | True | 1209.5 | 1163 | 1.0399828030954428 | 1.0012342196205657 | False |
| default-small-dynamic10 | Consume | False | 29 | 28 | 1.0357142857142858 | 1 | False |
| default-small-dynamic50 | Query | True | 1253.5 | 1274.5 | 0.98352295017653979 | 0.97422630404553268 | True |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 1.0089285714285714 | False |
| default-small-dynamic90 | Query | True | 1349 | 1363 | 0.98972853998532651 | 1.1027574971338912 | False |
| default-small-dynamic90 | Consume | False | 141 | 140 | 1.0071428571428571 | 1.2109999999999999 | False |
| finite-middle-mask1-oneway | Query | True | 32635.5 | 33220 | 0.98240517760385315 | 0.99373480200552011 | True |
| finite-middle-mask1-oneway | Consume | False | 69 | 68 | 1.0147058823529411 | 1.2681818181818179 | False |
| finite-middle-mask1-mutual | Query | True | 32698.5 | 32982.5 | 0.99138937315242937 | 0.99684160337647987 | True |
| finite-middle-mask1-mutual | Consume | False | 7 | 6 | 1.1666666666666667 | 1.2857142857142858 | False |
| finite-middle-mask4-oneway | Query | True | 33738.5 | 33890.5 | 0.99551496732122569 | 0.99255595591409818 | True |
| finite-middle-mask4-oneway | Consume | False | 246 | 245 | 1.0040816326530613 | 0.88526170798897952 | False |
| finite-middle-mask4-mutual | Query | True | 34130 | 34414 | 0.99174754460394021 | 1.1349400616789271 | False |
| finite-middle-mask4-mutual | Consume | False | 129 | 128 | 1.0078125 | 1.5751748251748243 | False |
| finite-middle-mask8-oneway | Query | True | 32097.5 | 32253 | 0.99517874306266085 | 0.9991523433696643 | True |
| finite-middle-mask8-oneway | Consume | False | 468 | 467 | 1.0021413276231264 | 1.0230350066050198 | False |
| finite-middle-mask8-mutual | Query | True | 31837.5 | 32154 | 0.99015674566150402 | 0.99912446785649978 | True |
| finite-middle-mask8-mutual | Consume | False | 464 | 465 | 0.99784946236559136 | 0.99508932351198054 | True |
| default-middle-dynamic10 | Query | True | 15664.5 | 16015 | 0.97811426787386824 | 1.0063352826510721 | False |
| default-middle-dynamic10 | Consume | False | 461 | 460 | 1.0021739130434784 | 1.0287738702151559 | False |
| default-middle-dynamic50 | Query | True | 17728 | 17865 | 0.992331374195354 | 0.99843906354236167 | True |
| default-middle-dynamic50 | Consume | False | 1753 | 1751 | 1.0011422044545975 | 1.0408510428479858 | False |
| default-middle-dynamic90 | Query | True | 17933.5 | 18242 | 0.98308847714066439 | 0.99595309011556143 | True |
| default-middle-dynamic90 | Consume | False | 2301 | 2300 | 1.0004347826086957 | 1.046632614453272 | False |
| finite-large-mask1-oneway | Query | True | 261759 | 263225.5 | 0.9944287312589396 | 1.005018685500485 | False |
| finite-large-mask1-oneway | Consume | False | 606 | 603 | 1.0049751243781095 | 0.95372539089700592 | False |
| finite-large-mask1-mutual | Query | True | 261570.5 | 262715.5 | 0.99564167321684482 | 0.99788903992605682 | True |
| finite-large-mask1-mutual | Consume | False | 29 | 28 | 1.0357142857142858 | 1.0277777777777777 | False |
| finite-large-mask4-oneway | Query | True | 280314 | 283000.5 | 0.99050708390974573 | 1.136452925633989 | False |
| finite-large-mask4-oneway | Consume | False | 2366 | 2350 | 1.0068085106382978 | 1.0733501434657853 | False |
| finite-large-mask4-mutual | Query | True | 282593 | 286227 | 0.98730378336075908 | 0.98770349603531593 | True |
| finite-large-mask4-mutual | Consume | False | 1201.5 | 1191 | 1.008816120906801 | 0.96019720624486415 | False |
| finite-large-mask8-oneway | Query | True | 259541 | 259842 | 0.99884160374381359 | 0.98617090413592357 | True |
| finite-large-mask8-oneway | Consume | False | 4424 | 4550 | 0.97230769230769232 | 0.98906970215617562 | True |
| finite-large-mask8-mutual | Query | True | 272517.5 | 266721 | 1.0217324470139209 | 1.0209763919574448 | False |
| finite-large-mask8-mutual | Consume | False | 4476.5 | 4549.5 | 0.98395428069018576 | 0.98237962156961556 | True |
| default-large-dynamic10 | Query | True | 248663.5 | 253797.5 | 0.97977127434273392 | 0.9792012183371015 | True |
| default-large-dynamic10 | Consume | False | 8536.5 | 8546.5 | 0.99882993038085766 | 0.9915813715455476 | True |
| default-large-dynamic50 | Query | True | 272876 | 279397 | 0.97666045089961595 | 0.97226206007552107 | True |
| default-large-dynamic50 | Consume | False | 33413 | 33818.5 | 0.98800952141579312 | 0.98057801204237738 | True |
| default-large-dynamic90 | Query | True | 283392.5 | 291169 | 0.97329214305094292 | 0.97879814156016165 | True |
| default-large-dynamic90 | Consume | False | 44885.5 | 44655.5 | 1.005150541366685 | 1.0057942977151877 | False |

Required metrics: 18/27 pass. Case reports: 27.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
