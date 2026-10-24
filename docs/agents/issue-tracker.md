# 任务跟踪：GitHub Issues

仓库：`ODtian/SomeEngine.Next`，地址为 https://github.com/ODtian/SomeEngine.Next 。需求规格、任务票和讨论使用该仓库的 GitHub Issues，通过 `gh` CLI 操作。

## 需求规格与任务票

- `to-spec` 将需求规格发布为一个 Issue，使用 `ready-for-agent` 标签。
- `to-tickets` 将规格拆成可独立验收的任务票，每张票对应一个 Issue，按依赖顺序创建。
- 任务票引用来源规格，并写明验收标准及阻塞它的任务票。使用 GitHub 的原生任务阻塞关系表达依赖。
- `triage` 按 `triage-labels.md` 中的角色名称管理任务状态。类别标签沿用 `bug` 和 `enhancement`。
- `implement` 与 `implement-spec` 从规格、任务票正文、讨论和依赖关系读取待实现的行为。

## 操作约定

在仓库目录调用 `gh`，或显式指定 `--repo ODtian/SomeEngine.Next`。

```bash
gh issue create --repo ODtian/SomeEngine.Next --title "<标题>" --body-file "<正文文件>" --label ready-for-agent
gh issue view <编号> --repo ODtian/SomeEngine.Next --comments
gh issue list --repo ODtian/SomeEngine.Next --state open --json number,title,body,labels
gh issue comment <编号> --repo ODtian/SomeEngine.Next --body-file "<评论文件>"
gh issue edit <编号> --repo ODtian/SomeEngine.Next --add-label "<目标标签>" --remove-label "<原状态标签>"
gh issue close <编号> --repo ODtian/SomeEngine.Next
```

多行正文写入文件，通过 `--body-file` 传入。临时正文文件在操作完成后按根级 `AGENTS.md` 的清理规则处理。

任务完成后在 Issue 中记录实现、验收结果与交付分支。通过 PR 交付时，在 PR 正文使用 `Closes #<编号>` 关联已完成的规格或任务票，使合并操作关闭相应 Issue。

## Triage 对象

处理对象为本仓库的 GitHub Issues。

**PRs as a request surface: no.**
