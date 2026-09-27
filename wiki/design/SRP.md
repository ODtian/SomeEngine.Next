# SRP

关注点分离。业务逻辑、IO、解析、校验、状态更新、展示保持边界清晰。

拆分以**变化原因**和**可测试性**为准。一个类/方法只有一个变化原因。

参见 [[DRY]]。

资产系统的职责分配见 [[Asset-Responsibilities]]；文件加载与对象生命周期见 [[Asset-Loading-and-Lifetime]]，工程导入和目录登记见 [[Asset-Import-and-Storage]]。
