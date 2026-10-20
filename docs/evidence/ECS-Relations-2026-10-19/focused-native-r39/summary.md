# focused-native-r39 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4254 | 4237.5 | 1.0038938053097346 | 0.97685985026961941 | False | True |
| finite-small-mask1-oneway | Consume | False | 10 | 9.5 | 1.0526315789473684 | 1.0833333333333333 | False | False |
| finite-small-mask1-mutual | Query | True | 4220.5 | 4232.5 | 0.99716479621972831 | 0.98663558663558659 | True | True |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1 | True | True |
| finite-small-mask8-mutual | Query | True | 4291.5 | 4309 | 0.99593873288466006 | 1.006353835737722 | False | True |
| finite-small-mask8-mutual | Consume | False | 60 | 59 | 1.0169491525423728 | 0.98880597014925375 | False | False |
| default-small-dynamic50 | Query | True | 1080 | 1087 | 0.99356025758969646 | 0.97384155455904331 | True | True |
| default-small-dynamic50 | Consume | False | 107 | 107 | 1 | 1.0065645514223194 | False | True |
| default-small-dynamic90 | Query | True | 1095 | 1087 | 1.0073597056117756 | 0.96295617091509256 | False | True |
| default-small-dynamic90 | Consume | False | 139 | 139 | 1 | 1.0502431118314424 | False | False |
| character-small-hair16-clustered | Query | True | 4369 | 4456 | 0.98047576301615802 | 0.97176029203001424 | True | True |
| character-small-hair16-clustered | Consume | False | 122 | 122 | 1 | 1.0033955857385399 | False | True |
| character-small-hair16-interleaved | Query | True | 4262.5 | 4489.5 | 0.94943757656754646 | 0.98963685065647922 | True | True |
| character-small-hair16-interleaved | Consume | False | 117 | 117 | 1 | 0.98015873015873012 | True | True |
| updates-small-sparse | MaintenanceAndQuery | True | 739.5 | 790 | 0.9360759493670886 | 0.83077588070023778 | True | True |
| updates-small-sparse | FrameTotal | True | 2035.5 | 2085 | 0.97625899280575534 | 0.97403393988959308 | True | True |
| threads-small-sparse-parallel | ScheduledPairOutput | True | 790.5 | 749 | 1.0554072096128171 | 1.065833829069998 | False | False |
| threads-small-sparse-parallel | Consume | False | 475 | 478 | 0.99372384937238489 | 0.89321357285429137 | True | True |
| default-middle-dynamic10 | Query | True | 15435 | 15695 | 0.98343421471806303 | 0.95147248958482977 | True | True |
| default-middle-dynamic10 | Consume | False | 482 | 474 | 1.0168776371308017 | 1.033717105263158 | False | False |
| default-middle-dynamic50 | Query | True | 16485 | 16565 | 0.99517054029580443 | 0.97238833927654678 | True | True |
| default-middle-dynamic50 | Consume | False | 1853 | 1854 | 0.9994606256742179 | 0.96607468123861562 | True | True |
| finite-large-mask8-mutual | Query | True | 268618.5 | 271380.5 | 0.98982240802121002 | 1.0104994280433546 | False | False |
| finite-large-mask8-mutual | Consume | False | 4995.5 | 4959 | 1.0073603549102641 | 1.0158654677503025 | False | False |
| default-large-dynamic50 | Query | True | 251727.5 | 252016.5 | 0.99885324968801648 | 0.9914266636489224 | True | True |
| default-large-dynamic50 | Consume | False | 34002.5 | 34020 | 0.99948559670781889 | 0.98548557353976074 | True | True |
| default-large-dynamic90 | Query | True | 261471.5 | 257452 | 1.0156126190513184 | 1.0143838093143414 | False | False |
| default-large-dynamic90 | Consume | False | 45298 | 45044 | 1.005638930823195 | 1.0009424089221655 | False | True |

Required metrics: 9/15 pass <=1.00; 12/15 pass <=1.01. Case reports: 14.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
