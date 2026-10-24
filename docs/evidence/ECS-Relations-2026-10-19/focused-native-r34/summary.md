# focused-native-r34 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-mutual | Query | True | 4263 | 4222 | 1.0097110374230223 | 0.94097515236755747 | False | True |
| finite-small-mask1-mutual | Consume | False | 3 | 3 | 1 | 1 | True | True |
| finite-small-mask8-oneway | Query | True | 4173.5 | 4149.5 | 1.0057838293770334 | 1.0097309673726389 | False | True |
| finite-small-mask8-oneway | Consume | False | 61 | 61 | 1 | 0.971830985915493 | True | True |
| default-small-dynamic10 | Query | True | 936 | 1010.5 | 0.92627412172191981 | 0.99476549413735338 | True | True |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1 | True | True |
| default-small-dynamic50 | Query | True | 1044 | 1112 | 0.9388489208633094 | 0.95070162481536191 | True | True |
| default-small-dynamic50 | Consume | False | 105 | 105 | 1 | 0.93165467625899279 | True | True |
| default-small-dynamic90 | Query | True | 1083 | 1152 | 0.94010416666666663 | 0.93612981184187816 | True | True |
| default-small-dynamic90 | Consume | False | 139 | 139 | 1 | 1.0133333333333334 | False | False |
| threads-small-sparse-parallel | ScheduledPairOutput | True | 760.5 | 794 | 0.95780856423173799 | 0.98722245496439043 | True | True |
| threads-small-sparse-parallel | Consume | False | 460 | 461.5 | 0.99674972914409532 | 0.93198529411764708 | True | True |
| finite-middle-mask8-oneway | Query | True | 34637.5 | 34631.5 | 1.0001732526745881 | 0.92868937764518145 | False | True |
| finite-middle-mask8-oneway | Consume | False | 506.5 | 506.5 | 1 | 1.0073509392866866 | False | True |
| default-middle-dynamic10 | Query | True | 13766 | 14803 | 0.92994663243937037 | 0.93476958985304726 | True | True |
| default-middle-dynamic10 | Consume | False | 462 | 462 | 1 | 0.98181049069373938 | True | True |
| default-middle-dynamic50 | Query | True | 16662.5 | 17929.5 | 0.9293343372654006 | 0.99049543196228673 | True | True |
| default-middle-dynamic50 | Consume | False | 1905 | 1900.5 | 1.0023677979479084 | 1.0105585831062671 | False | False |
| default-middle-dynamic90 | Query | True | 17339.5 | 18644 | 0.93003110920403342 | 0.94525490758330644 | True | True |
| default-middle-dynamic90 | Consume | False | 2470.5 | 2482.5 | 0.99516616314199391 | 1.0138691322901849 | False | False |
| finite-large-mask8-oneway | Query | True | 261629.5 | 262509.5 | 0.99664774036749149 | 0.9876753482047832 | True | True |
| finite-large-mask8-oneway | Consume | False | 4657.5 | 4639 | 1.0039879284328519 | 1.0587909552376558 | False | False |
| finite-large-mask8-mutual | Query | True | 268264 | 264563 | 1.01398910656441 | 0.98967502551154185 | False | False |
| finite-large-mask8-mutual | Consume | False | 4738 | 4689 | 1.0104499893367456 | 0.98247063940843848 | False | False |
| default-large-dynamic10 | Query | True | 216555.5 | 237300 | 0.91258112094395283 | 0.91642613009543061 | True | True |
| default-large-dynamic10 | Consume | False | 8181.5 | 8257.5 | 0.99079624583711778 | 1.0149319166494739 | False | False |
| default-large-dynamic50 | Query | True | 248004 | 270472.5 | 0.91692870809416849 | 0.8974904140990525 | True | True |
| default-large-dynamic50 | Consume | False | 34649.5 | 34554.5 | 1.0027492801227047 | 1.0021777178051812 | False | True |
| default-large-dynamic90 | Query | True | 260369.5 | 284520.5 | 0.91511683692387724 | 0.91300461585338177 | True | True |
| default-large-dynamic90 | Consume | False | 45588.5 | 45703.5 | 0.99748378132965743 | 0.99016968348268397 | True | True |
| character-large-hair64-clustered | Query | True | 1004601 | 1030593.5 | 0.97477909573464228 | 0.96940810073765538 | True | True |
| character-large-hair64-clustered | Consume | False | 44067 | 44393.5 | 0.99264531969770353 | 1.003373438003178 | False | True |

Required metrics: 12/16 pass <=1.00; 15/16 pass <=1.01. Case reports: 16.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
