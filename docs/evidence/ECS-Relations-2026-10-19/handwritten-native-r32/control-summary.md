# handwritten-native-r32 handwritten controls

H1 and H2 use the same handwritten implementation with independent side state and the same actual workspace. Required=false; these ratios are diagnostic data.

| Case | Boundary | H1 type | H2 type | H1 median ticks | H2 median ticks | H1/H2 median | H1/H2 p95 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| finite-large-mask1-mutual-control | Query | HandwrittenMutualCandidatesSide | HandwrittenMutualCandidatesSide | 263141 | 262912.5 | 1.0008691104454903 | 1.0033608742052229 |
| finite-large-mask1-mutual-control | Consume | HandwrittenMutualCandidatesSide | HandwrittenMutualCandidatesSide | 28 | 28 | 1 | 1.0588235294117647 |
| default-large-dynamic50-control | Query | HandwrittenDefaultSide | HandwrittenDefaultSide | 279085.5 | 279357.5 | 0.9990263372202286 | 0.99198340216270875 |
| default-large-dynamic50-control | Consume | HandwrittenDefaultSide | HandwrittenDefaultSide | 34238 | 33938.5 | 1.0088247860099886 | 1.0180152508647053 |
