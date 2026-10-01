# Raster Execution

## Definition
Indirect draw/dispatch 只是 Cluster triangle raster pipeline 当前内部命令机制，不是 Cluster 对外业务语义。

## Invariants
- Cluster 产生 bounded work sets 并按 stage compatibility grouping。
- Cluster 自己拥有 SW/HW raster command recording；调用方不安装 raster command callback，也不选择如何发 draw/dispatch。
- 当前内部可以使用 indirect draw/dispatch；将来具体 pipeline/backend 可以改用 mesh shader、work graph 或其它机制，而不改变 Cluster 对外 geometry/material 语义。
- 不公开只为替换底层命令方式而存在的 raster executor 对象。

## Relations
[[Hardware-Raster]] · [[Software-Raster]] · [[architecture/material/Raster-Compatibility]]

目标页硬件光栅先根据当前窗口、页请求、页表和接收区域解析 atlas 像素地址，
通过筛选的像素再调用用户的可见性 shader，并将其深度原子合并到目标像素。
地址解析和写入共用原页表及 atlas，用户 shader 继续解释自己的 varying 和深度。

目标页 mesh 光栅将用户 shader 输出的位置投影到当前光栅窗口。整个三角形的
保守边界位于实际请求页窗口之外时，输出退化三角形，使光栅器提前丢弃。
跨越投影 w 边界的三角形沿原路径处理。位置暂存属于当前 mesh 工作组。

目标页 mesh 光栅同时复用工作组页缓存，将三角形保守像素边界与请求页的接收区域
遮罩相交。边界向相邻像素扩展，覆盖光栅量化误差；最终像素继续检查请求和接收
区域。页缓存按工作组线程索引分配写入项，只初始化当前窗口包含的页，读写共用
原页表、代数和遮罩。超过缓存尺寸的窗口沿逐像素页解析路径处理。
