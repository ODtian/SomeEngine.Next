# Final source PackageReference validation

Source: 51c8ce75949cf16d7190e14028c655f526d5697b. The same feed/cache and consumer Release output were refreshed. Fourteen actual stages used seven public queue tasks; eight no-build pack commands ran in one exclusive task with each command, UTC interval and exit preserved.

Actual closure: 6 direct PackageReference entries, 8 restored SomeEngine packages. Fresh generated output: 27 files, 78 thin Where/Except entries, 17 storage registrations, 2 schema registrations. Relations library: 9 fixed generated files, 41 declared types.

| Stage | Public queue | Source | Exit |
| --- | --- | --- | ---: |
| BuildPackages | 21b8f27309a94c259b38d14f5bd89192 | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| Pack:SomeEngine.Diagnostics | 85b23478035843e0be2e3e8f7a24e0bd | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| Pack:SomeEngine.Job.Contracts | 85b23478035843e0be2e3e8f7a24e0bd | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| Pack:SomeEngine.Job | 85b23478035843e0be2e3e8f7a24e0bd | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| Pack:SomeEngine.ECS.SourceGen | 85b23478035843e0be2e3e8f7a24e0bd | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| Pack:SomeEngine.ECS | 85b23478035843e0be2e3e8f7a24e0bd | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| Pack:SomeEngine.ECS.Relations | 85b23478035843e0be2e3e8f7a24e0bd | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| Pack:SomeEngine.ECS.Systems | 85b23478035843e0be2e3e8f7a24e0bd | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| Pack:SomeEngine.Physics | 85b23478035843e0be2e3e8f7a24e0bd | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| RestoreConsumer | 01372c533eef42feab5f7eeac6ce5a4d | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| RebuildConsumer | 0f985a0162d04f169a988fbe404b4838 | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| RunConsumerJit | 37edd2edd37e48b58e4d53d3b4a9eaa5 | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| PublishConsumerNative | 249d36807dc840139d4efd93d8c52ec8 | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |
| RunConsumerNative | d5af5248f43c4571ad9890a57823e2ba | 51c8ce75949cf16d7190e14028c655f526d5697b | 0 |

The managed and native consumers both printed their real PASS output for arities 1–8, Item8, ref output, schema, typed/generated/query access, edges, key, bitmap, spatial maintenance, parallel fill and consumption. NumberPointValue and weighted eight-component results are asserted by that consumer and covered in the selected final IL/native evidence.

Native publish retains its actual IL2070 diagnostic in the queue stdout. The registered alias metadata and its actual consumers are included in the selected IL evidence.

Raw stage commands and exit facts: [package-stages.json](package-stages.json), [eight pack commands](package-pack-stages.json), [actual package and generator facts](package-facts.json). Each of the seven public tasks has result/stdout/stderr under queues/.
