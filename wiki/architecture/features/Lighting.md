# Lighting

Lighting 是独立 rendering feature，负责 light extraction、light resources、spatial grouping、cookie resources 与 lighting policy。

具体 Shade implementation 可以消费 Lighting 提供的真实资源，这些资源通过 shader reflection 映射成普通 RenderGraph accesses；Cluster core 不建立 light grid。

通用多灯分组按 clear → build → compact 执行。Build 沿每个灯的 cell 范围，以 `GroupsPerLight` 将 cell 交错分给工作组。每组先统计本组 cell 数，再通过 CAS 循环从同一全局共享预算发起一次饱和预留；组内线程按连续 ordinal 使用接受的 link 区间，并保持原 cell 分区步长。每个 cell 继续写入链表头和成员计数，Compact 将这些成员写入同一预算容量的索引列表。

共享预算用于全部 view 和局部灯。预算耗尽时，分配计数饱和到 link 容量，overflow 标志记录被省略的成员；着色与局部阴影请求按 cell 的实际成员数消费索引。单点光的 Prepare/Fill 入口继续生成直接索引网格。实现入口为 `assets/Shaders/clustered_light_grid.slang` 与 `LightingFeature.Record`。

DefaultGame 保留编译后的 lighting loose 资产，并将同一资产构建为 `Library/engine-shaders.assetpack`。Sponza 与 Bistro 的 `Library/AssetPacks/packs.json` 先列出该共享包的相对路径，再列出原 `Library/scene.assetpack`。既有 `AssetContent` 与 `AssetPackStorage` 按顺序选择首个匹配的资产身份，使三景读取同一份 lighting 内容；原场景包继续提供几何、纹理与其它资产。共享包通过既有 `--pack <DefaultGame 内容根> <共享包路径> --asset <lighting GUID>` 入口构建。
