# Structural Copy on Write

结构 candidate 与当前 World 共享未修改 backing；只有第一次真实写入对应 page/chunk/row backing 时才分离。

Entity-location pages、table chunks、DynamicBuffer overflow、shared-component backing 等各自按实际写入粒度实现 copy-on-write。未触碰数据、equal write、empty prepare 不应产生无意义 copy。

Copy-on-write 只改变结构更新成本，不改变 [[Structural-Transactions]] 的一次提交语义或旧 reader 对旧数据的可见性。

sparse 的实体数组、值数组、写版本数组与索引页由同一份存储持有。结构事务共享这份存储，第一次可写访问将它们一起分离。同一存储的首次分离通过串行入口完成，并行 Job 的各行写引用随后使用同一份已分离数组，保留所有行的值与写版本。
