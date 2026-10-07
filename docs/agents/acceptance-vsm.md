# VSM 页管理与阴影采样

规格：[#189](https://github.com/ODtian/SomeEngine.Next/issues/189)。参照 Epic UE5.8.3 `396c9f059903aed5fec78ecd3d437a40c6415368`，本轮源码基点为 `600dd84f02dd7ce17957f3354faa34313ac8c298`。

用户确认的三张实现票正在推进：

- [#191 工作容量、材质范围与阴影灯处理](https://github.com/ODtian/SomeEngine.Next/issues/191)：差异 7、11、15。
- [#192 页表、深度数组与缓存请求组织](https://github.com/ODtian/SomeEngine.Next/issues/192)：差异 13、16–19、41、42、44、45。
- [#193 像素内局部灯遍历与低开销软阴影采样](https://github.com/ODtian/SomeEngine.Next/issues/193)：差异 21–24。SMRT 默认关闭，替代采样采用每 VSM family 最多 4 次遮挡深度查询加 8 次过滤。

待定差异 1、2、3、4、8、14、20、29–30、35、39–40、43、46–49 的分析和决定集中在 #189。次表面、毛发、面光、Froxel 等特殊表面处理由调用渲染管线负责；VSM 提供通用页管理与采样。

复用已有公开渲染、缓存移动和跨页采样行为，按实际修改执行最小必要检查。三票集成后统一构建，性能记录归属 #190/#81。
