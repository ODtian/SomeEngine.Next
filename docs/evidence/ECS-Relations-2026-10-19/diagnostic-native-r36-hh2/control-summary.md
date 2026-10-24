# diagnostic-native-r36-hh2 handwritten controls

H1 and H2 use the same handwritten implementation with independent side state and the same actual workspace. Required=false; these ratios are diagnostic data.

| Case | Boundary | H1 type | H2 type | H1 median ticks | H2 median ticks | H1/H2 median | H1/H2 p95 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-small-mask1-mutual-control | Query | HandwrittenMutualCandidatesSide | HandwrittenMutualCandidatesSide | 4114.5 | 4089 | 1.0062362435803376 | 1.0080805844587115 |
| finite-small-mask1-mutual-control | Consume | HandwrittenMutualCandidatesSide | HandwrittenMutualCandidatesSide | 2 | 2 | 1 | 1.0833333333333333 |
| finite-large-mask8-oneway-control | Query | HandwrittenOneWayCandidatesSide | HandwrittenOneWayCandidatesSide | 262185.5 | 262354.5 | 0.9993558334238597 | 0.99028854286766876 |
| finite-large-mask8-oneway-control | Consume | HandwrittenOneWayCandidatesSide | HandwrittenOneWayCandidatesSide | 4700 | 4678.5 | 1.0045954900074809 | 1.0323459459459459 |
