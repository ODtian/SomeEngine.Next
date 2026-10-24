# Resident Page Address

Resident page address 是当前 device-local PageHeap 中可被 shader 使用的 byte offset。

它只在 page 已安装且 leaf 已切换后对新 traversal 可见；evict 后旧 frame 可以继续使用旧地址直到相关 queue completions 结束。

参见 [[Page-Install]] · [[Page-Eviction]]。