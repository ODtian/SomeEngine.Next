# controlled-reads-r30 handwritten controls

H1 and H2 use the same handwritten implementation with independent side state and the same actual workspace. Required=false; these ratios are diagnostic data.

| Case | Boundary | H1 type | H2 type | H1 median ticks | H2 median ticks | H1/H2 median | H1/H2 p95 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-large-mask1-mutual-control | Query | HandwrittenMutualCandidatesSide | HandwrittenMutualCandidatesSide | 275907 | 276093.5 | 0.99932450419875873 | 0.99644920262334746 |
| finite-large-mask1-mutual-control | Consume | HandwrittenMutualCandidatesSide | HandwrittenMutualCandidatesSide | 29 | 29 | 1 | 0.97297297297297303 |
| default-large-dynamic50-control | Query | HandwrittenDefaultSide | HandwrittenDefaultSide | 295250.5 | 295030 | 1.0007473816222079 | 0.99614245750650265 |
| default-large-dynamic50-control | Consume | HandwrittenDefaultSide | HandwrittenDefaultSide | 35882 | 35866 | 1.0004461049461886 | 0.97959257771885311 |
