# Breaking cutover 与完整交付  [[architecture/ECS-Architecture|ECS Architecture]]


在当时的 cutover 决策点，已知用户均在仓库内，且没有已发布 workload 依赖这些旧接口，
因此采用 deliberate breaking cutover。此句只记录当时的兼容性判断；当前仓库已经有 Core、
Render、Systems、serialization、benchmark、fuzz 与 NativeAOT smoke 等实际 consumer：

- 不保留本次 cutover 已点名旧 ECS API 的 source、binary 或 runtime symbols；
- 不提供 obsolete wrapper、转接层、feature flag或新旧双 ownership model；
- 若仓库外另行提供机械源码改写工具，它也不能通过重新声明旧 symbol让旧代码继续编译；
- 仓库内 product code、tests、samples、serialization integration、SourceGen、wiki和harness在同一 end-to-end delivery中全部迁移；
- 只有新 owner/borrow/structural-root模型成为唯一产品路径、旧 API 搜索结果归零且完整 harness通过，才算完成；
- 不能只交付底层 JobSystem后把 ECS query、Relation、Hierarchy、CommandBuffer或System迁移留作后续 handoff。

同样不保留 pre-cutover ECS serialized World、scene、delta、snapshot或测试 fixture兼容：

- 唯一普通序列化 wire 是 version 4；任何其他 envelope version 都 fail closed，不做 best-effort转换；
- 不为 `RelationStore`、`RelationTag`、`ChildBuffer`、`HierarchyLink`或旧 entity-location布局编写 importer；
- 每个 type key只有一个非零 64-bit schema fingerprint；unknown type/topology、zero/mismatch fingerprint、旧32-bit字段、旧Guid/string编码、length-prefix frame和unversioned delta section全部拒绝；
- item与topology codec直接向最终sink编码一次并追加length footer；不存在dry pass、第二次encode、encoded frame backing、topology snapshot DTO或mmap副本；
- `WorldCheckpointCodec`只接受`SEWCP003`，其128-byte header包裹一次相同的canonical `RawCheckpoint` World payload；不存在section directory、checkpoint专用component/topology codec、NativeRaw dump或non-seekable整包暂存；
- `ReadWorld`直接构造一个新的最终World；slot、component/buffer、hierarchy shard和relation generation边读边进入最终backing，失败即Dispose。不存在`LoadInto(existing World)`、candidate apply、capture DTO或失败原子Apply兼容入口；
- runtime不提供schema转换注册表、unknown skip或旧primitive decoder。schema/wire变更是breaking data change；产品若要转换已发布文件，只能另行提供runtime之外的显式离线工具。

在该决策发生时，仓库没有需要保留的已发布 ECS consumer 或持久化 workload，因此兼容层、
旧数据转换器和双格式 reader 不属于那次 cutover 的产品交付。当前 durable-save 和其他
serialization workload只接受各自的current envelope、current schema和认证模式；不会恢复
任何pre-cutover或未来旧版本格式兼容。

