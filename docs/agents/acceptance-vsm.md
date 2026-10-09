# Cluster 管线实现与验收

规格：[#189](https://github.com/ODtian/SomeEngine.Next/issues/189)。固定参照为 Epic UE5.8.3 `396c9f059903aed5fec78ecd3d437a40c6415368`。构建、流送、裁剪、分箱、光栅、用户表面及 VSM 在同一生产管线接合，主视图与阴影视图共享 Cluster 实现。

当前 #189 继续验收，[PR #136](https://github.com/ODtian/SomeEngine.Next/pull/136) 为草稿。真实来源 BLAS/TLAS、原始三角形与 UV alpha、实际提交相机和变换形成 RT GT；全部有效接收像素的平均误差与错误像素比例以原 2% 阈值直接决定测试成败。误影、漏影的原始像素计数及各子集比例完整留存。

当前集成 `07ed5fe0` 包含方向光阻挡搜索光源锥支持域修复 `83c24671` 与旧七项测试的实际 VSM visibility 观察 `6b6ae092`。原材质形成的接收点、法线、顶点、透明裁剪及全部有效像素继续参与原源 RT 比较。

| 本次原用例（2048 RT 样本） | 平均误差 | 错误像素比例 | 接收覆盖 | 结果 |
| --- | ---: | ---: | ---: | --- |
| 默认 1024 小球软阴影 | 1.5673187% | 1.8828545% | 99.74187% | 通过 |
| directional-grid 软阴影 | 1.525943% | 1.8054428% | 99.74522% | 通过 |

两项 self hit 均为 0；网格保留全部 136587 个原接收像素，包括原图像相减无法测量的 1153 个零直射光像素。其余六项观察迁移用例与四项局部光行为继续验收，Bistro 的硬/软阴影误差继续驱动修复。证据位于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/performance-190-current/rt-gt/directional-source-cone/` 与 `raw-standard-visibility/`。[#189 本次结果](https://github.com/ODtian/SomeEngine.Next/issues/189#issuecomment-6072490457)。以下保留各提交原始结果。
源码 `a8225b96` 修复完整层级遍历、接收面世界坐标调用、共用 HW/SW/细分正交深度精度、QEM 重复顶点焊接与沿边最小二乘、持久材质输入、工作容量错误报告和独立接收像素写入。小球在原 0.25px 下由每球 68 cluster / 4026 三角形降至 6 / 366，来源几何与属性保持。采样按实际遮挡深度计算物理距离；页请求、投影和采样共用用户几何法线判断接收资格，表面偏移及原斜率偏置使用用户着色法线。

当前默认软阴影直接使用原始来源 BLAS/TLAS、2048 次面积光源积分、实际提交相机及变换，保留全部有效接收像素、半影和边界。逐像素可见性误差大于 0.15 记为错误；平均误差与错误比例各须 ≤2%，接收覆盖须 ≥98%，自身命中为 0。

| 当前行为 | 平均误差 | 错误像素比例 | 接收覆盖 | RT 测试 |
| --- | ---: | ---: | ---: | --- |
| 默认 1024 小球软阴影 | 1.60115% | 2.09083% | 99.7419% | 失败 |
| 默认 Sponza 软阴影 | 0.67322% | 0.93687% | 100% | 通过 |
| 默认 Amazon Bistro 软阴影 | 6.32167% | 8.16700% | 99.9986% | 失败 |
| Point，来源半径 0.5 | 2.1503398% | 5.2519284% | 100% | 失败 |
| Spot，来源半径 0.5 | 2.2288267% | 5.331645% | 100% | 失败 |

上表自身命中均为 0。三景证据位于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/performance-190-current/rt-gt/qem194-soft-final/`，局部光证据位于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/rt-gt/local-current/baseline/`。小球、Bistro 与局部光失败继续驱动通用采样修复；硬阴影及既有公开 RT 行为随对应修复完成验收。

本轮 QEM 三景资产已通过正式 `Game.Build` / glTF 入口完成来源重建：DefaultGame 81 项项目资产、Sponza 96 项来源资产、Amazon Bistro 2046 项来源资产与 1532 个实例；来源文件及 `.meta` 身份和参数保持原字节。内容构建源码为 `b8eab4a78138db1d82ff18683ef611b7efb2d485`，当前 shader 与 App 源码为 `a8225b96000ccd478ac0bd3beb2e725d53378931`，App 构建 0 警告 / 0 错误；Default 的 68 项 shader 通过正式 AssetCook shader 入口构建，两景共享与场景 shader 通过 `--refresh-scene-shaders` 更新。三景全部网格 SHA256 与 QEM 正式交付记录一致，几何身份单列于 `geometry-files.json`。正式内容、构建结果、来源身份及全部登记资产 SHA256 保存在 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/cluster-189/qem-assets-194/` 的 `content/`、`build-results.json`、`source.json`、`source-metadata.json`、`identity.json` 与 `content-files.json`；三景资产构建状态均为 `Complete`。

局部光源修复 `8348ec37261e28181cf31316f49f1b2855d7b88c` 统一原页请求与读取的 nominal mip，receiver mask 保持 cell 覆盖职责；SourceRadius 与有效遮挡距离决定发光范围，原 8 纹素配置用于有限查询支持，等面积对称盘保持 4/8 查询预算。原 Point/Spot 两行在 source radius 0.5、2048 等固角样本、完整接收域和原阈值下 2/2 通过：平均误差 1.0303537% / 1.1301176%，错误像素比例 1.28015755% / 1.3762866%，接收覆盖均 100%、self hit 均 0。原始 red/green 图像、CSV、比较及解析支持数据保存在 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/rt-gt/local-current/`；测试规则检查 0 违规。

源码 `cb6b531ae6261f206ea01d5746c64d806f98b109` 的既有硬阴影、公开来源比较与局部光 RT 批次完成 24 项：21 通过、3 失败；4 项局部光启动/恢复及 Spot 范围行为等待修复后补齐。六行硬阴影中的小球与 Sponza 两种模式共 4 行通过，Bistro 默认/诊断模式错误比例分别为 4.3210853% / 4.3386668%；七项公开来源比较中 6 项通过，directional-grid 软阴影平均误差 2.0353781%、错误比例 2.293044%；已完成的 11 项局部光行为均通过。原 2% 判定及全部有效接收像素保持，测试规则检查 0 违规。批次在完成结果边界停止，逐项结果、4 项待验收名称、原 App/资产身份及失败图像保存于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/rt-gt/final-hard-local/` 的 `progress.json`、`entrypoint-identity.json` 与各 case 目录。

同目录 `opaque-caster-lod.json` 记录 132 个 Opaque 来源 caster 的公共资产解码与纹素射线：原来源深度全部在 2 bit 内复现，叶层及初始 Cluster 视图 0.25 阈值下均 132/132 命中，选中 cluster 最大投影误差 0.24986263，阴影几何比例 0.5。CPU 选择器使用 `ChildReference` 指定的几何页；早期使用 group 请求页范围产生的 122/10 诊断已由此结果纠正。实际 GPU 后继 `vsm_view_compact.slang` 将阈值覆写为 `MaxPixelsPerEdge=1`；`actual-shadow-lod.json` 按最终 1/0.5 重算相同 132 个当前纹素，目标 caster 的双面几何射线 127 条命中、120 条选中深度与实际 R32 在 2 bit 内吻合。

实际 root、candidate、分箱和写入读数进一步定位两处 HW 获胜三角形：来源 mesh829 的 page0/cluster7/triangle18 写入 `1056965498`，来源 mesh1194 的 page0/cluster6/triangle13 写入 `1056964460`；正式几何计算与实际 atlas 同值。前者的 LOD2 轮廓移入 0.479 个阴影纹素，命中点距原源 20.24mm、小于原 OwnError 59.57mm；后者保持原源深度。mesh1195 的最终 37 个 cluster、1098 个三角形中，正面轮廓移开 0.165 个纹素，原双面交点 `1056964645` 对应被裁剪的背面，资格已在 `near-view6-source.json` 修正。Native group 极值、共同 LOD bounds、误差编码及外部组边界锁规则与固定参照一致。

`current-source-frontface.json` 与 `current-hard-slope-margin.json` 保存原 132 个实际接收点的来源射线和深度数学：当前 77 个漏影代表中 75 个原射线有正面遮挡，当前接收点射线有 66 个，按原 Native normal bias 偏移后有 46 个；21 条正面遮挡移开、1 条移入。原 slope2 和 R32 表示区间重算与 132 条实际硬判定全部吻合，其中 10 个漏影由 slope bias 翻为亮区。CPU 来源中心重现原判定 130 条，另 2 条及全部原 RT 像素保留。原阈值和待验收 case 继续驱动 Bistro 与 directional-grid 的源修复。

原 49 项对应如下；各票正文记录具体算法、数据关系与消费者。

| 原编号 | 交付归属 |
| --- | --- |
| 1、13、14、43、49 | #198 自动缓存、接收区域层级与 LOD 失效；13 复用 #192，49 接 #195 |
| 2、3、4、8、12、31、37 | #196 裁剪、分箱、HW/SW 光栅与工作记录 |
| 5 | Cluster 内部容量保持 64 顶点 / 124 三角形 |
| 6、9、46、47 | #194 分区、简化、层级、页布局和 VRB |
| 7、11、15 | #191 工作容量、材质范围与完整阴影灯集合 |
| 10、25、29、30、36 | #197 用户表面复用、接收资格及采样输入 |
| 16、17、18、19、41、42、44、45 | #192 页表、驻留映射、深度数组、HiZ、页池及缓存请求 |
| 20 | #199 视图近端坐标、UV/法线变换和原点迁移后的页地址 |
| 21、22、23、24 | #193 像素内局部灯、结果打包和 4+8 软采样；噪声输入接 #200 |
| 26、27、28、32、33 | #197 调用者提供透明、水、头发、次表面、面光、Froxel 的特殊语义 |
| 34 | SMRT 搁置，当前采样沿 #193 |
| 35、38、39、40 | #200 分辨率偏置、噪声、预过滤顺序及实际页工作列表 |
| 48、49 | #195 依赖、安装/卸载 fixup、粗细交接与叶子语义 |
| 全部 49 项 | #201 原始来源全量重建、当前格式与统一三场景交付 |

三项保留边界：Cluster 内部容量为 64 顶点 / 124 三角形；用户 shader 解释自己的顶点、材质、可见性与表面；SMRT 搁置，软采样每个 VSM family 最多 4 次遮挡深度查询加 8 次过滤。特殊表面语义由调用渲染管线提供，VSM 维护通用页管理和采样。

#201 在源码 `e3a97e66c1c78dea5f4136f6d12393a1e52207ad` 完成汇合：页扩展材质表统一 4 字节对齐读取，VS/MS/SW/细分近端投射、prefilter 历史坐标、primary/secondary 页资格及带区共同接合，流送状态区间按 cluster header/240B 节点维护。引擎根目录保留 shader 库和原始 IcoSphere GLB/来源身份，默认资产由 DefaultProject 的唯一正式入口生成；资产存储 benchmark 与测试夹具同步读取该目录。

正常入口从来源构建 81 项默认资产、96 项 Sponza、2046 项 Amazon Bistro（1532 实例），场景几何、运动与材质算法保持。完整解决方案构建 0 警告/0 错误，公开资产输入/范围读取 2/2、材质更新 D3D12 HW/SW 2/2、远平移 Auto/prefilter 1/1、粗细交接 1/1 通过，自动规则检查 0 违规；两轴评审的原始发现及新增 hunk 复核均完成修正。三景 Complete/Measured，反馈与 1/2/3ms 目标记录在性能交付页。

三场景性能采用 [性能交付记录](acceptance-performance.md) 的框架计时，RenderGraph 验收沿用 [#70 交付记录](acceptance-70.md)。当次构建、行为、原始报告及身份保存于 `F:/SomeEngine.AgentWork/rg70-delivery-evidence/final/cluster-189/ticket-201/`。
