# optimized-13 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 3925 | 3935.5 | 0.99733197814763053 | 1.010512902198152 | False |
| finite-small-mask1-oneway | Consume | False | 9 | 10 | 0.90000000000000002 | 1 | True |
| finite-small-mask1-mutual | Query | True | 4125 | 4151 | 0.99373644904842207 | 0.98851220038868481 | True |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1.3333333333333333 | False |
| finite-small-mask4-oneway | Query | True | 4315 | 4296 | 1.0044227188081936 | 1.3779131621735523 | False |
| finite-small-mask4-oneway | Consume | False | 31 | 31 | 1 | 1.4586255259467049 | False |
| finite-small-mask4-mutual | Query | True | 4336.5 | 4330 | 1.001501154734411 | 1.0330417374803198 | False |
| finite-small-mask4-mutual | Consume | False | 17 | 16 | 1.0625 | 1.0526315789473684 | False |
| finite-small-mask8-oneway | Query | True | 4042 | 4088 | 0.98874755381604695 | 0.99569668612637374 | True |
| finite-small-mask8-oneway | Consume | False | 59 | 58 | 1.0172413793103448 | 1.0257812499999996 | False |
| finite-small-mask8-mutual | Query | True | 4090.5 | 4207 | 0.97230805799857378 | 0.6320651834151837 | True |
| finite-small-mask8-mutual | Consume | False | 58 | 58 | 1 | 0.7092198581560285 | True |
| default-small-dynamic10 | Query | True | 1118 | 1142 | 0.978984238178634 | 0.95416971853091981 | True |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1 | True |
| default-small-dynamic50 | Query | True | 1240 | 1256 | 0.98726114649681529 | 0.97630671092885501 | True |
| default-small-dynamic50 | Consume | False | 106 | 105 | 1.0095238095238095 | 1 | False |
| default-small-dynamic90 | Query | True | 1285 | 1309 | 0.98166539343009929 | 1.0368585199886586 | False |
| default-small-dynamic90 | Consume | False | 139 | 139 | 1 | 1.013157894736842 | False |
| character-small-hair16-clustered | Query | True | 4289.5 | 4298 | 0.99802233597021872 | 1.0306103028252411 | False |
| character-small-hair16-clustered | Consume | False | 119 | 117 | 1.017094017094017 | 1.0211999999999999 | False |
| character-middle-hair64-interleaved | Query | True | 61225 | 59574.5 | 1.0277048065867107 | 1.0190558112923973 | False |
| character-middle-hair64-interleaved | Consume | False | 2028 | 2057 | 0.98590179873602335 | 0.96923389142567218 | True |
| finite-large-mask4-mutual | Query | True | 285683.5 | 281561 | 1.0146415874357599 | 1.0006858570628996 | False |
| finite-large-mask4-mutual | Consume | False | 1102 | 1085.5 | 1.0152003684937816 | 0.94806792169189336 | False |
| default-large-dynamic10 | Query | True | 246126.5 | 250947.5 | 0.98078881040855159 | 0.97929255554091854 | True |
| default-large-dynamic10 | Consume | False | 7938.5 | 7927 | 1.0014507379841049 | 0.99823609751781883 | False |
| default-large-dynamic50 | Query | True | 272762.5 | 279410.5 | 0.97620705020033249 | 0.98142765952443844 | True |
| default-large-dynamic50 | Consume | False | 32826.5 | 33280 | 0.98637319711538463 | 0.99545953066002835 | True |
| default-large-dynamic90 | Query | True | 284480 | 292062.5 | 0.97403809116199447 | 1.0019762998607857 | False |
| default-large-dynamic90 | Consume | False | 44286 | 44822.5 | 0.98803056500641417 | 1.0060499712457844 | False |
| character-large-hair16-interleaved | Query | True | 1144137 | 1144810 | 0.99941212952367642 | 0.99287884608114563 | True |
| character-large-hair16-interleaved | Consume | False | 43583.5 | 43793.5 | 0.99520476783084244 | 0.97961929545156923 | True |

Required metrics: 8/16 pass. Case reports: 16.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
