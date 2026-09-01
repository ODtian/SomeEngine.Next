# Page Identity

Page ID 是 Cluster geometry protocol 中长期可引用的页标识，不等于设备 page heap 中的字节位置。

Nonresident leaf 保存 fault marker；resident leaf 保存当前 page heap address。页重新装入不同地址时 Page ID 不改变。

参见 [[Resident-Page-Address]] · [[Page-Faults]]。