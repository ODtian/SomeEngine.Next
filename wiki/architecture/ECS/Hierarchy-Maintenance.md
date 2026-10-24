# Immediate hook 与 deferred maintenance system  Parent: [[Hierarchy]] 

Parent lifecycle mutation支持两种由caller选择的 maintenance timing：

- **Immediate**：Parent add/replace/remove apply时运行 ECS internal relationship hook；hook调用共享parent-transition kernel，从old applied parent移除child、加入new parent的Children、维护local order并推进internal applied-parent。操作返回时Parent与Children一致。
- **Deferred**：只发布已验证的canonical Parent及其native change facts；Children保持last-applied inverse。generated job的 `ref Parent<D>` / writable Span天然属于此路径。之后统一hierarchy maintenance system按受影响parents分组，调用同一个transition kernel批量追平Children与applied-parent。

internal applied-parent是ECS维护状态，只记录Children当前反映的三态值（no parent或具体parent）；它不是public component model、第三份authoring truth或change stream。Immediate path同步推进它，因此maintenance后来观察到 `current Parent == applied parent` 时no-op；Deferred path保留它，因而即使Parent在maintenance前被多次改写或移除，system仍知道应从哪个old Children shard删除。Parent永远赢，system不从stale Children反推canonical truth。

relationship hook只维护 hierarchy invariant。它是ECS静态注册的internal hook，不是可替换的用户delegate，也不执行SomeUI `NodeHooks`、layout、mount、render或任意业务副作用。那些反应由普通systems/jobs消费native changes后产生 `LayoutDirty`、`DrawOrderDirty`、`MountPending` 等普通components。

同步World mutation和CommandBuffer都可在命令真正apply时选择Immediate或Deferred；CommandBuffer只决定transport/playback时间。raw ref/span不可能在每次字段assignment时拦截old/new target，所以固定Deferred。需要当前Children的consumer显式依赖对应maintenance handle；有意读取旧应用态的consumer只声明relaxed Children read而无需该semantic dependency。读取Children绝不隐式触发maintenance或隐藏write。

两条路径共享同一validation、transition、order和change-publication primitives，不复制两套ordered/unordered engines。Immediate mutation在返回前验证；Deferred ref/span owner在释放资源前对最终Parent preimage/overlay执行forest validation，失败或body fault时恢复其owned Parent rows并fault/cancel handle，因而不会把self-parent、dead-parent或cycle发布给后继owner。maintenance只修复合法Parent到Children的freshness，不负责猜测或容忍非法canonical topology。

