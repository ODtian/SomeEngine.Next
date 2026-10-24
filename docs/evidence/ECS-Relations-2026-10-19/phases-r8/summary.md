# phases-r8 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- |
| updates-small-sparse | IndependentMaintenance | False | 601 | 600 | 1.0016666666666667 | 0.90334938704028023 | False |
| updates-small-sparse | IndependentQuery | False | 278.5 | 341 | 0.81671554252199419 | 0.79899413243922901 | True |
| updates-small-sparse | IndependentConsume | False | 163 | 251 | 0.64940239043824699 | 0.95423311292622848 | True |
| updates-small-uniform | IndependentMaintenance | False | 636 | 621 | 1.0241545893719808 | 1.1085591347831143 | False |
| updates-small-uniform | IndependentQuery | False | 1918 | 2894 | 0.66275051831375265 | 0.64887303768234594 | True |
| updates-small-uniform | IndependentConsume | False | 454.5 | 456 | 0.99671052631578949 | 1.0356970961013956 | False |
| updates-small-dense | IndependentMaintenance | False | 641.5 | 659.5 | 0.9727065959059894 | 0.9691348937631874 | True |
| updates-small-dense | IndependentQuery | False | 10473.5 | 16193.5 | 0.6467718529039429 | 0.65815367322543739 | True |
| updates-small-dense | IndependentConsume | False | 2235.5 | 2260 | 0.98915929203539821 | 0.97689830413820056 | True |
| updates-middle-sparse | IndependentMaintenance | False | 2268.5 | 2260.5 | 1.0035390400353903 | 1.0388146830842981 | False |
| updates-middle-sparse | IndependentQuery | False | 501.5 | 709 | 0.70733427362482371 | 0.78754034530457495 | True |
| updates-middle-sparse | IndependentConsume | False | 180 | 181 | 0.99447513812154698 | 0.96560196560196565 | True |
| updates-middle-uniform | IndependentMaintenance | False | 2355.5 | 2350 | 1.0023404255319148 | 0.99300836850010743 | False |
| updates-middle-uniform | IndependentQuery | False | 8991 | 12984 | 0.69246765249537889 | 0.70614983854964641 | True |
| updates-middle-uniform | IndependentConsume | False | 2890 | 2967 | 0.9740478597910347 | 1.0017389218684416 | False |
| updates-middle-dense | IndependentMaintenance | False | 2375.5 | 2367 | 1.0035910435149979 | 0.95728555462419662 | False |
| updates-middle-dense | IndependentQuery | False | 39301.5 | 60930.5 | 0.64502178711810998 | 0.64325630198964212 | True |
| updates-middle-dense | IndependentConsume | False | 7912 | 7845 | 1.008540471637986 | 0.99782696040427588 | False |
| updates-large-sparse | IndependentMaintenance | False | 8826 | 8917.5 | 0.9897392767031119 | 0.95414721898094135 | True |
| updates-large-sparse | IndependentQuery | False | 1366 | 2216 | 0.61642599277978338 | 0.60535079513564094 | True |
| updates-large-sparse | IndependentConsume | False | 205 | 206 | 0.99514563106796117 | 0.89208504556012203 | True |
| updates-large-uniform | IndependentMaintenance | False | 10107.5 | 10220.5 | 0.9889437894427866 | 0.98595448320074608 | True |
| updates-large-uniform | IndependentQuery | False | 63540.5 | 97147 | 0.65406548838358369 | 0.66020204963815643 | True |
| updates-large-uniform | IndependentConsume | False | 11477 | 11619 | 0.98777863843704272 | 0.9904637430493326 | True |
| updates-large-dense | IndependentMaintenance | False | 9598 | 10057 | 0.95436014716118123 | 0.95754132691707872 | True |
| updates-large-dense | IndependentQuery | False | 158392 | 245322.5 | 0.64564807549246395 | 0.63942800995016258 | True |
| updates-large-dense | IndependentConsume | False | 30763 | 30775.5 | 0.99959383275657587 | 1.0007692919065394 | False |

Required metrics: 0/0 pass. Case reports: 9.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
