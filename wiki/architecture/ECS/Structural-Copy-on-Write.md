# Structural Copy on Write

结构 candidate 与当前 World 共享未修改 backing；只有第一次真实写入对应 page/chunk/row backing 时才分离。

Entity-location pages、table chunks、DynamicBuffer overflow、shared-component backing 等各自按实际写入粒度实现 copy-on-write。未触碰数据、equal write、empty prepare 不应产生无意义 copy。

Copy-on-write 只改变结构更新成本，不改变 [[Structural-Transactions]] 的一次提交语义或旧 reader 对旧数据的可见性。