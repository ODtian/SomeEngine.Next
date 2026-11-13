# Virtual Shadow Page Compaction

`VirtualShadowPageCompactionPasses` 登记可用页列表的 Count、共享扫描和 Fill 三段持久图。调用方提供已经完成当前请求分类的 `ShadowPageLists`、页容量和已加载 shader；返回值只包含图 ID，由 VSM feature 在提交成功后保存。

输入 LRU 段按上一帧请求顺序排列。当前已请求或已回收的位置为无效页标记，EMPTY 段保存回收页。`CountAvailableShadowPages` 为每个 LRU 位置写入有效标志，在 `prefix[capacity]` 写零 sentinel，并把 EMPTY 的数量暂存到 `prefix[capacity + 1]`。

前缀缓冲共 `capacity + 2` 个 uint，共享 `ScreenBinningScanPasses` 只扫描前 `capacity + 1` 项。扫描后 `prefix[capacity]` 是缓存页总数，最后一项保留原 EMPTY 数量。

`FillAvailableShadowPages` 按前缀把缓存页压紧到 AVAILABLE 段，然后原序追加 EMPTY 段，并写 AVAILABLE 数量、清空 EMPTY 数量。缓存页保持 LRU 的相对顺序，空页位于末尾。参数缓冲使用设备对齐的 Upload 声明，由每帧显式参数写入填写。

当前辅助可供 F3 替换页池 phase 2。既有 `BuildVirtualShadowPagePool` 的 phase 2 保留到该接入完成；此辅助只持有自己登记的参数、前缀、视图和 pass ID，`ShadowPageLists` 仍由 VSM 持有。

`VirtualShadowPageCompactionTests` 使用 513 个页位置进行公共 GPU 对照：穿插有效缓存页和空页，核对原缓存顺序、空页尾部排列及空页数量消费。该检查证明 compaction 数据行为，正式页淘汰和阴影回退图像由 BEH-V9 验收。
