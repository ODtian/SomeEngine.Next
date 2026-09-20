# 参考实现取舍  [[architecture/ECS-Architecture|ECS Architecture]]


- Unity Entities：采用 `IJobEntity`、`in/ref` access inference、`RefRO/RefRW`、typed lookup 和 deterministic parallel command思想；不采用任意关闭 parallel restriction 或 frame-global dependency 心智。
- Bevy ECS：采用 query borrow、Parent source-of-truth、derived Children 和 unique-subtree propagation proof。
- Unreal Mass：采用 query/chunk/range contention，而不是 system-wide stage。
- Flecs：采用 per-thread staged commands 和 stable partitions。
- Taskflow/Rayon/oneTBB：采用 structured descendants、cooperative wait 和 external activity lifetime。

外部证据：

- [Unity IJobEntity](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/iterating-data-ijobentity.html)
- [Unity SystemAPI.Query](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/systems-systemapi-query.html)
- [Unity ComponentLookup](https://docs.unity3d.com/Packages/com.unity.entities@1.4/api/Unity.Entities.ComponentLookup-1.html)
- [Unity deterministic ECB playback](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/systems-entity-command-buffer-playback.html)
- [Bevy Query](https://docs.rs/bevy_ecs/latest/bevy_ecs/system/struct.Query.html)
- [Bevy ChildOf source of truth](https://docs.rs/bevy/latest/bevy/ecs/prelude/struct.ChildOf.html)
- [Bevy transform propagation](https://github.com/bevyengine/bevy/blob/main/crates/bevy_transform/src/systems.rs)
- [Roslyn source generators are additive, not call-site rewriters](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.cookbook.md)
- [Rayon structured scope](https://docs.rs/rayon/latest/rayon/fn.scope.html)
- [oneTBB external async node](https://oneapi-spec.uxlfoundation.org/specifications/oneapi/latest/elements/onetbb/source/flow_graph/async_node_cls)
