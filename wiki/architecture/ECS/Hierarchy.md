# Hierarchy  [[architecture/ECS-Architecture|ECS Architecture]]


hierarchy以statically typed domain区分独立 forests。每个domain公开同一组 ECS-native relationship shape：`Parent<TDomain>` 是一个child的canonical at-most-one-parent truth，`Children<TDomain>` 是由全部 Parent反推得到、可query但只读的 `RelationshipTarget` component/view。World的default domain只提供省略domain参数的 `Parent` / `Children` convenience facade，不另造一套storage或maintenance engine。同一Entity可在多个domains拥有互相独立的Parent关系；各domain分别做cycle validation并具有独立job resource identity。

hierarchy core没有membership概念：不存在 `HierarchyNode<D>`、Join、Leave或“全World属于某domain”的隐式状态。root只对具体 workload candidate set有意义：

```text
workload candidate + Without<Parent<D>> = 该 workload 的 root
workload candidate + With<Parent<D>>    = 该 workload 的 child
```

candidate由Transform、UI、scene或其他workload自己的普通components/query定义，也可包含当前Parent/Children关系中出现的entities。一个没有Parent、没有Children且不匹配任何workload candidate的isolated Entity，在hierarchy core中没有“member/non-member”差异。core因此不提供脱离candidate query的全局membership或无条件`GetRoots<D>()`；需要显式顶层集合的workload可以使用自己的root component或anchor Entity。

`Parent<D>` 是公开普通component并保留完整 native query/change语义。`Children<D>` 不是第二份authoring truth；用户只能通过 `RefRO`、scope-bound read-only span/view或可选snapshot读取，不能取得其 writable ref/span。empty Children的物理presence可由storage/policy优化决定，不能被用作membership tag。`ChildBuffer`、`HierarchyLink`及业务可写Depth全部删除。


## Related concepts

- [[Hierarchy-Maintenance|Immediate hook 与 deferred maintenance system]]
- [[Hierarchy-Change-Tracking|Native change tracking]]
- [[Hierarchy-Children|Parent-local ordered/unordered Children]]
- [[Hierarchy-Mutation-and-Traversal|Mutation、destroy 与可选 traversal snapshot]]
