# WorkReleased  Parent: [[Job-Completion]] 

`WorkReleased` 表示该 owner 的 body、固定 parallel batches 或 external activity 已结束，资源访问和 pins 已释放。它不等待动态 children。

resource hazard 只等待 `WorkReleased`。这样父 body 释放资源后，无关 child 不会阻塞资源 successor。

