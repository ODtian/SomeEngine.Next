# 最终实际八包使用方的编译证明

源码：`c0effa290eee225c296f779a85b9d79109d789a5`。本页索引实际产物和原始读取，正文说明见[包交付记录](../../../ECS-Relations-Packages-AOT-2026-10-19.md#243-最终实际包刷新)。

[14个生产阶段](package-stages.json)全部Exit0，归于Libraries `b29fc3fd3ed94505adb27bead7eed42c`及Consumer `0943bc78afa94cbd816e22a2a2350079`两个公共任务。JIT和Native实际执行均通过1–8元、Item8、加权204、借用输出与提前结束、元数据及完整串并行消费；首轮5对、更新3对，累计2,2,0,2。

[包与生成事实](package-facts.json)记录6个直接引用、8个恢复包，8份nuspec均指向本页源码；消费生成27文件/78薄入口，库固定族9文件/41类型。[实际IL](package-selected-methods.il)和[登记记录](package-registration-and-packages.json)确认17项存储、2项schema登记及其查询、空间和Job调用路径。

[四函数原文](package-four-functions.raw.asm)各选中一次，对应[实际符号](package-native-symbols.json)及[完整统计](package-native-statistics.json)：

- WeightedTotal8：`0x20dd0–0x20e2f`，32条，95B。
- ComponentRelation八元绑定：`0xe0860–0xe15e9`，907条，3465B。
- Number点引用读取：`0x197ea0–0x198293`，300条，1011B。
- 空间并行实体对填充：`0x1d4180–0x1d502a`，987条，3754B。

各项用于定位其承担的不同工作。[读前后产物事实](package-frozen-inputs.json)为DLL 69,120B、UTC01:23:34.4962378Z，Native对象24,930,157B、UTC01:23:50.7250812Z。

[初次读者](package-initial-reader-stage.json)沿用r38旧产物时间和尺寸，输入门控返回2，[原父任务回执](package-initial-parent-result.json)保留Exit2。修正读者从本轮products按实际路径取得时间和尺寸，沿原14个成功生产阶段读取同一批产物。续接CPU任务[5bc38d23ffdd4c2fa48b9a115101dbbb](package-codegen-queue-result.json)于01:29:14.3362221Z–01:29:59.5279665Z返回Exit0，覆盖性能归档及IL/四函数读取。[完整事实](package-codegen-facts.json)和[修正命令](package-codegen-corrected-command.txt)关联这些结果。
