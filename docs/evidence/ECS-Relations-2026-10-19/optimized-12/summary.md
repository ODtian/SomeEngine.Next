# optimized-12 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-oneway | Query | True | 4141 | 4078 | 1.0154487493869544 | 0.78353197508983929 | False |
| finite-small-mask1-oneway | Consume | False | 10 | 10 | 1 | 0.78078078078078184 | True |
| finite-small-mask1-mutual | Query | True | 4040.5 | 3977.5 | 1.0158390949088623 | 0.99622250915027799 | False |
| finite-small-mask1-mutual | Consume | False | 2 | 2 | 1 | 1.25 | False |
| finite-small-mask4-oneway | Query | True | 4283 | 4195 | 1.0209773539928486 | 1.0898129574410405 | False |
| finite-small-mask4-oneway | Consume | False | 30 | 30 | 1 | 1.0294117647058822 | False |
| finite-small-mask4-mutual | Query | True | 4366.5 | 4199 | 1.0398904501071684 | 1.1147376349485314 | False |
| finite-small-mask4-mutual | Consume | False | 16 | 16 | 1 | 1 | True |
| finite-small-mask8-oneway | Query | True | 4171 | 4103 | 1.0165732390933464 | 1.025355138822253 | False |
| finite-small-mask8-oneway | Consume | False | 61 | 59 | 1.0338983050847457 | 1.3952779893373943 | False |
| finite-small-mask8-mutual | Query | True | 4106 | 4016 | 1.0224103585657371 | 1.0220308938971892 | False |
| finite-small-mask8-mutual | Consume | False | 59 | 58 | 1.0172413793103448 | 1.046875 | False |
| default-small-dynamic10 | Query | True | 1158 | 1130 | 1.024778761061947 | 1.0434321646620113 | False |
| default-small-dynamic10 | Consume | False | 28 | 28 | 1 | 1.0689655172413792 | False |
| default-small-dynamic50 | Query | True | 1285.5 | 1270 | 1.0122047244094488 | 0.96135688604033132 | False |
| default-small-dynamic50 | Consume | False | 107 | 106 | 1.0094339622641511 | 0.99145299145299148 | False |
| default-small-dynamic90 | Query | True | 1341 | 1353 | 0.99113082039911304 | 0.99617958984076516 | True |
| default-small-dynamic90 | Consume | False | 143 | 143 | 1 | 0.94616090547568221 | True |
| character-small-hair16-clustered | Query | True | 4550 | 4511.5 | 1.008533747090768 | 1.0005420982053694 | False |
| character-small-hair16-clustered | Consume | False | 117 | 116 | 1.0086206896551724 | 1.0130819545979226 | False |
| character-middle-hair64-interleaved | Query | True | 58174.5 | 61057 | 0.95279001588679435 | 0.95735519410160674 | True |
| character-middle-hair64-interleaved | Consume | False | 2024 | 2036.5 | 0.99386201816842623 | 0.9851837998462275 | True |
| finite-large-mask4-mutual | Query | True | 289593.5 | 282414.5 | 1.0254200828923443 | 1.0338192804178401 | False |
| finite-large-mask4-mutual | Consume | False | 1140.5 | 1085.5 | 1.0506678949792723 | 1.0454974925503306 | False |
| default-large-dynamic10 | Query | True | 251706 | 251605.5 | 1.0003994348295249 | 1.0080615115343801 | False |
| default-large-dynamic10 | Consume | False | 7996.5 | 8069.5 | 0.99095359068095912 | 1.0163186447338259 | False |
| default-large-dynamic90 | Query | True | 292544.5 | 292061 | 1.0016554760820513 | 1.0090838886354758 | False |
| default-large-dynamic90 | Consume | False | 44860.5 | 44877.5 | 0.99962119101999891 | 1.0075771499656812 | False |
| character-large-hair16-interleaved | Query | True | 1135703 | 1210008 | 0.93859131509874316 | 0.95466693923470491 | True |
| character-large-hair16-interleaved | Consume | False | 43662.5 | 43412.5 | 1.0057587100489491 | 1.0046722590399342 | False |

Required metrics: 3/15 pass. Case reports: 15.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
