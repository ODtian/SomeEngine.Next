# 衣物样例的人体更新

本页记录独立衣物样例已经实现的公开更新与数据职责。入口为 [ClothingScene](../../../tools/SomeEngine.Clothing.Demo/ClothingScene.cs)、[BodyMesh](../../../tools/SomeEngine.Clothing.Demo/BodyMesh.cs)、[BodyMotion](../../../tools/SomeEngine.Clothing.Demo/BodyMotion.cs) 和 [显示端](../../../tools/SomeEngine.Clothing.Demo/wwwroot/demo.js)。人体与服饰来源由 `CharacterAsset` 读取 MakeHuman 数据，动作来源记录在样例的 `Assets/Motion/NOTICE.txt`，资源许可随对应资产保存。

`ClothingScene.Advance(DemoControls)` 先计算本次骨骼输入，再调用 `BodyMesh.Update(motion, dualQuaternion, seconds)`。BodyMesh 保留前一时刻位置，选择既有 DQS 或 LBS 计算，每段顶点通过 `JobSystem.ExecuteParallel` 写入各自的位置与法线。方法在所有范围完成后统一更新足部旋转；调用方随后可以读取人体数据、更新轨迹目标与显示数据。

`BodyMesh.UpdateContactSurface(blend)` 保留前一时刻接触位置，分范围插值前后人体位置。范围全部完成后，由协调阶段更新 `SurfaceTriangles` 和足部表面。`FindContact` 读取该表面的插值位置、当前人体法线，以及前后位置在本次时间步内形成的速度。骨骼、人体顶点和表面分别由其原对象持有，同步范围的输入与结果覆盖整个使用阶段。

样例显示使用 `ClothingScene.Bytes` 中的公开帧数据：`HeaderLength` 后先存人体位置，再存人体法线，每项为 XYZ 三个 float。帧填充位于人体和衣物阶段完成之后，显示端按同一协议消费。DQS/LBS 的显示帧、接触插值与速度检查见 [BodyUpdateTests](../../../tests/SomeEngine.Clothing.Tests/BodyUpdateTests.cs)，运动包围面检查见 [BodyShellBehaviorTests](../../../tests/SomeEngine.Clothing.Tests/BodyShellBehaviorTests.cs)。
