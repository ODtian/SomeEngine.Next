# Parent-local ordered/unordered Children  Parent: [[Hierarchy]] 

sibling order是一个domain内每个parent的direct Children collection的局部策略，不是整张hierarchy graph的模式，也不沿ancestor/descendant继承。一个child所在collection的策略与该child自己作为parent时采用的策略无关。

ordered parent shard保存该membership所需的order metadata并支持append/prepend、stable child anchor before/after、block splice、complete permutation和显式policy conversion。unordered parent shard只保存packed child entities，允许swap-remove，不保存per-child order key、order index、monotonic sequence或renumber state，不承诺稳定枚举，也不在iteration inner loop dispatch ordered logic。因此unordered parent不为ordered capability付storage、mutation或iteration成本。

进入ordered parent而没有显式placement的Immediate mutation默认按已线性化的apply顺序append。Deferred batch也使用append语义；多个同时进入者按`(recorded stable producer key, stable logical child identity)`排序，没有producer key的raw ref/span mutation使用stable logical child identity，绝不采用worker完成顺序、change scan顺序或packed storage顺序。要求精确位置的caller必须使用placement-capable typed mutation。进入unordered parent不读取或保留旧sibling position。

已有children的parent可以显式转换local policy。unordered -> ordered接受完整child permutation；未提供时只在本次conversion scratch按stable logical child identity形成一次性canonical初始顺序，绝不提升incidental packed order。ordered -> unordered丢弃order metadata。转换只触及目标parent shard；无关parent，尤其unordered shards，不付费。普通Children view对两种shard都提供scope-bound read-only traversal并只承诺membership；有序index/reorder API必须先验证该parent当前具有ordered capability。

