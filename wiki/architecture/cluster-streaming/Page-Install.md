# Page Install

## Sequence
1. 为新 page 分配尚未被 traversal 发现的 free range。
2. async I/O / decode 写 staging。
3. RenderGraph copy staging → PageHeap range。
4. copy 完成后 patch BVH leaf → resident page address。
5. 后续 traversal 才能发现该 page。

## Invariants
page bytes 必须先可读，再使 leaf 指向它。上传 fresh range 可以与旧 frame 对其它 resident ranges 的读取并行。

## Relations
[[Page-Heap]] · [[BVH-Leaf-Patching]] · [[Page-Eviction]]