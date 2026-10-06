PageLists 资源声明顺序接合

公开行 `GeometryStreamingPriorityTests.APageReadBudgetLoadsTheGreaterProjectedDetailDemandFirst` 首次提交帧时，统一 validation 报告 `ShadowPageLists` 位于绑定0而实际传入 ConstantBuffer。实际结果 1 failed / 0 passed / 0 skipped，保留在 page-list-binding-red.trx/log。

初始化、页池、页分配、动态分辨率四个 shader 将 PageLists include 放到对应 Feature 绑定的数据位置。随后同一公开行完成整帧执行，进入实际页面 I/O 的优先级断言；最终该行 green 为 1 passed / 0 failed / 0 skipped，见 green.trx/log。绑定修正与优先级行为的证据分别记录。
