# final-phases-r38 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| updates-small-sparse | IndependentMaintenance | False | 638.5 | 647 | 0.98686244204018547 | 0.99897092873681503 | True | True |
| updates-small-sparse | IndependentQuery | False | 190.5 | 250 | 0.76200000000000001 | 0.8218599033816425 | True | True |
| updates-small-sparse | IndependentConsume | False | 150 | 152 | 0.98684210526315785 | 1.0363057324840765 | False | False |
| updates-small-uniform | IndependentMaintenance | False | 657 | 655 | 1.0030534351145037 | 1.0488474204171241 | False | False |
| updates-small-uniform | IndependentQuery | False | 1898.5 | 2886.5 | 0.65771695825394072 | 0.6802906448683016 | True | True |
| updates-small-uniform | IndependentConsume | False | 350.5 | 355 | 0.98732394366197185 | 1.0016246953696182 | False | True |
| updates-small-dense | IndependentMaintenance | False | 661 | 662.5 | 0.99773584905660373 | 1.0168765068309671 | False | False |
| updates-small-dense | IndependentQuery | False | 11650.5 | 16347.5 | 0.7126777794769843 | 0.71897968632444009 | True | True |
| updates-small-dense | IndependentConsume | False | 2190 | 2280 | 0.96052631578947367 | 0.95793064241046044 | True | True |
| updates-middle-sparse | IndependentMaintenance | False | 2232.5 | 2292 | 0.97404013961605584 | 0.97227036395147315 | True | True |
| updates-middle-sparse | IndependentQuery | False | 418 | 653 | 0.64012251148545174 | 0.69609665427509293 | True | True |
| updates-middle-sparse | IndependentConsume | False | 170 | 169 | 1.0059171597633136 | 1.0294540229885059 | False | False |
| updates-middle-uniform | IndependentMaintenance | False | 2336.5 | 2355 | 0.9921443736730361 | 1.0443895295625114 | False | False |
| updates-middle-uniform | IndependentQuery | False | 9126 | 13026 | 0.70059880239520955 | 0.72615760964529097 | True | True |
| updates-middle-uniform | IndependentConsume | False | 2809.5 | 2897.5 | 0.96962899050905949 | 0.98131803471609302 | True | True |
| updates-middle-dense | IndependentMaintenance | False | 2422 | 2464 | 0.98295454545454541 | 0.97959009393680618 | True | True |
| updates-middle-dense | IndependentQuery | False | 43509.5 | 61625 | 0.7060365111561866 | 0.70656968255239005 | True | True |
| updates-middle-dense | IndependentConsume | False | 7706.5 | 7738.5 | 0.99586483168572726 | 0.99349989843591302 | True | True |
| updates-large-sparse | IndependentMaintenance | False | 8862.5 | 9033 | 0.98112476475146682 | 0.98855136253216924 | True | True |
| updates-large-sparse | IndependentQuery | False | 1410.5 | 2282.5 | 0.61796276013143481 | 0.65653637553768474 | True | True |
| updates-large-sparse | IndependentConsume | False | 214 | 212 | 1.0094339622641511 | 1.1673970201577564 | False | False |
| updates-large-uniform | IndependentMaintenance | False | 11538 | 11550.5 | 0.99891779576641704 | 0.99631811487481592 | True | True |
| updates-large-uniform | IndependentQuery | False | 69604.5 | 102523 | 0.67891595056718979 | 0.70088688620313555 | True | True |
| updates-large-uniform | IndependentConsume | False | 12336.5 | 12242.5 | 1.0076781703083522 | 1.0896651846474912 | False | False |
| updates-large-dense | IndependentMaintenance | False | 10004 | 10146.5 | 0.98595574828758681 | 0.9541667554494897 | True | True |
| updates-large-dense | IndependentQuery | False | 178211 | 249889.5 | 0.71315921637363711 | 0.72167824970768424 | True | True |
| updates-large-dense | IndependentConsume | False | 31327.5 | 30946.5 | 1.0123115699675247 | 0.98122265301485623 | False | False |

Required metrics: 0/0 pass <=1.00; 0/0 pass <=1.01. Case reports: 9.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
