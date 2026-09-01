# Scheduler  Parent: [[review/ECS-Validation|ECS Validation Checklist]] 

- pending explicit dependency不预占资源，later ready conflict可前进；
- parent/pending dependent/dynamic child反例不死锁；
- semantic self/ancestor/transitive/combined-handle cycles被拒绝；
- WorkReleased 与 ScopeCompleted 分离；
- Execute 的 before/after admission race、zero-worker pumping、body fault cleanup；
- disjoint/overlap/whole ranges、whole Read + subrange Write mode map、多资源反向声明、数千 intervals 不退化为 O(N²)；
- topology prepare在 resolve/cancel/Closing/seal fault时完整 rollback，且未 seal owner不可运行；
- A/B 与 B/A 跨 World submissions按 sorted composite prepare前进；
- old runtime handles不提前 completed，cross-generation dependency拒绝。

