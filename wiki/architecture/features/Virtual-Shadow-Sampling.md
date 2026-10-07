# Virtual Shadow Sampling

当前主投射采用有限查询的 contact-hardening PCSS。每个普通 VSM family 先查询中心和三个
旋转三角形位置来估计遮挡者距离，再以八个圆盘位置过滤；最大深度查询数为 12。四次查询
均得到无遮挡深度时直接返回可见，全部遮挡时继续过滤。零尺寸光源通过一次精确深度比较
形成结果。额外第一人称 family 独立采样与合成，因此方向光同时采样两个 family 时上限为 24。

方向光使用 `tan(SourceAngleRadians / 2) * blockerSeparation` 计算世界半影半径；局部光使用
`SourceRadius * blockerSeparation / blockerDistance`。过滤与遮挡搜索半径都限制在配置的
纹素范围内。默认 blocker/filter 数量为 4/8，最大纹素半径为 8；主投射调用软采样入口，
默认 SMRT ray count 为 0。

每次查询依据同一接收者平面计算目标纹素中心的深度。相同 source view 复用已算出的
平面梯度，祖先或相邻 cube face 使用各自投射。跨页查询消费完成深度与祖先规则；
跨 cube face 查询把目标 UV 对应的光射线与接收者平面求交，再选择对应 face。
每个查询沿祖先规则读取；有效查询参与同一过滤累积，缺页查询继续保留无效资格。
过滤可见性以实际有效过滤数量归一化，`Valid` 单独记录有效查询。整段过滤查询均不可用时
使用空投射语义，其他有效查询形成的软过滤结果继续保留。

局部光在每个像素内准备一次用户 receiver、世界位置和 light grid，然后遍历关联局部灯。
结果按实际阴影灯索引形成 32 灯一组的 `uint4`，每光使用 4 bits；第 33 灯开始写下一组。
32 灯以内在寄存器中累计一次写出，更大灯数先初始化本像素的全部组，再在同次 grid 遍历
中更新对应结果。所有组保持完整覆盖。

量化前对部分阴影加入幅度 `1/15` 的蓝噪声抖动，再四舍五入到 0–15。用户材质通过
`ReadVirtualShadowProjectedVisibility` 或额外层读取入口取得解码后的可见性。方向光继续
保存 FP32 结果。32B 用户 receiver 保留为请求、投射与最终材质共用的主要数据。

采样实现位于 `assets/Shaders/vsm_soft_sampling.slang`，像素投射与打包位于
`vsm_project_lights_common.slang`，读取位于 `vsm_projected_sampling.slang`。Feature 负责
绑定与按实际灯数建立当帧结果缓冲，用户 shader 解释材质和表面数据。
