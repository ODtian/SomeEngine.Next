# final-jit-r38 measurements

E/H ratios use the original double precision values. Each row includes its actual source commit, backend and public queue command in index.json. Raw JSON includes all paired samples, variance, throughput, allocations, cold execution, memory and equivalent results.

| Case | Boundary | Required | E median ticks | H median ticks | E/H median | E/H p95 | Pass <=1.00 | Pass <=1.01 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| finite-middle-mask4-mutual | Query | True | 31104 | 30306 | 1.026331419520887 | 1.0094165352556883 | False | False |
| finite-middle-mask4-mutual | Consume | False | 129 | 128 | 1.0078125 | 0.98872785829307563 | False | True |
| default-middle-dynamic50 | Query | True | 8697 | 8455.5 | 1.0285612914670925 | 1.006035917434611 | False | False |
| default-middle-dynamic50 | Consume | False | 1889 | 1891.5 | 0.99867829764736982 | 0.995089642571657 | True | True |
| character-middle-hair16-clustered | Query | True | 73940.5 | 73696.5 | 1.0033108763645493 | 0.90396932882466297 | False | True |
| character-middle-hair16-clustered | Consume | False | 2186 | 2191 | 0.99771793701506162 | 1.0301148999258709 | False | False |
| bitmap-space-middle-uniform-mutual | Query | True | 6293.5 | 8441 | 0.74558701575642694 | 0.58573687934407126 | True | True |
| bitmap-space-middle-uniform-mutual | Consume | False | 3856.5 | 3854 | 1.0006486766995331 | 0.9780244173140954 | False | True |
| complete-middle-uniform | Query | True | 8295.5 | 10216 | 0.81201057165231005 | 0.79717999654517191 | True | True |
| complete-middle-uniform | FrameTotal | True | 11370 | 13256.5 | 0.85769245275902384 | 0.84147364887546161 | True | True |
| complete-middle-uniform | Consume | False | 3037 | 3039.5 | 0.99917749629873331 | 0.96717632206480575 | True | True |
| updates-middle-uniform | MaintenanceAndQuery | True | 11999.5 | 14088.5 | 0.85172303651914683 | 0.2296623678292625 | True | True |
| updates-middle-uniform | FrameTotal | True | 17577.5 | 20155 | 0.87211610022326969 | 0.88884353278679229 | True | True |
| consume-middle-uniform | PairsAndConsume | True | 11524 | 13264 | 0.86881785283474067 | 0.21817482230669377 | True | True |
| consume-middle-uniform | Consume | False | 3117 | 3090.5 | 1.0085746642938036 | 0.99979815649599679 | False | True |
| threads-middle-uniform-parallel | ScheduledPairOutput | True | 9373 | 11470 | 0.81717523975588491 | 0.25947692523824017 | True | True |
| threads-middle-uniform-parallel | Consume | False | 3916.5 | 3946 | 0.99252407501267104 | 0.93220513348078804 | True | True |
| character-space-middle-clustered | Query | True | 66860 | 70173 | 0.95278810938680114 | 0.95921872732733338 | True | True |
| character-space-middle-clustered | Consume | False | 59680 | 59855 | 0.99707626764681312 | 1.0011848387871345 | False | True |

Required metrics: 8/11 pass <=1.00; 9/11 pass <=1.01. Case reports: 9.

Memory.csv records each actual live/addressed/capacity payload and count separately. Cold.csv preserves seven alternating setup/index/first-query/first-consumption pairs per case. Metrics.csv records both distributions and throughput; raw compressed JSON preserves the individual frame allocation and process-memory readings.
