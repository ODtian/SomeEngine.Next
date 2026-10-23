# 导航详细块流送

`NavigationTileSource<TTile>` 是独立持有来源的普通对象。`Directory` 给出小型块元数据，`Retain` 为新读者保持来源，`ReadAsync` 取得一个块的全部编码数组，`Decode` 在调用工作线程上构造完成数据。`ScheduleRead` 把异步读取完成信号桥接为 `IJobExternalFence`，随后提交声明实际字节数组的解码 Job。返回的 `NavigationTileRead<TTile>.Completion` 覆盖整个读取、解码与来源释放过程；`Complete` 交付普通资源引用。

网格资产的具体入口为 `NavigationMeshBlock.CreateTileSource(selection)`。来源公开同一构建结果的粗图与独立详细块，编码细节见[导航资产](Assets/Asset-Navigation.md)。`NavigationSceneRegion.Graph` 使用放置后的世界坐标图；TileSource 提供本地详细数据，流送接入 Job 根据该区域的 Transform 转换实际数组。

## 兴趣、接入与驻留

Heading 表示的专用区域图、TileSource、ASA 局部查询与相同兴趣/完成句柄接缝见[朝向导航的显式流送](Heading-Navigation.md#朝向区域摘要与显式流送)。

`NavigationStreaming(scene, scheduler)` 保持区域对应的来源。`AddSource(region, source)` 取得自己的来源引用，`Interests` 接受普通 `NavigationInterest`。应用更新兴趣的实际 `Position`、`Radius`、可选 `Direction`、`LookAheadDistance` 和当前 `Path`。附近范围、前方位置、路线前方入口和 `Path.RequiredTiles` 共同产生详细需求。`ResidentTileBudget` 按块数限制目录驻留，当前需求按实际距离排序，已有数据以最近使用顺序保留。

`SetSource(region, source)` 安装该区域后续读取使用的来源引用。动态构建完成后，可取得 `NavigationRegionSources.CreateTileSource()` 并传入此入口；完整流程见[动态导航](Navigation-Dynamic.md)。已经提交的读取继续持有自己的实际来源。

`Update` 提交所需读取，并接入当次已经完成的结果。`Reads` 公开各块的后台完成句柄。`Applied` 只覆盖本次已经取得数据的短接入 Job；运动调度把该句柄作为就绪依赖。接入 Job 在 Scene 写访问内取得当前目录，并声明该值的实际数组，按目录块内容版本匹配完成数据。接入一次交换完整 `NavigationSceneContent`，推进绑定 `Version` 和 `ResidenceVersion`，内容重建使用独立的 `ContentVersion`。预算移除目录引用后，已提交 Job 与路径分段继续持有实际数组。

应用将 `NavigationStreamingSystem` 放入单独的 `SystemGroup<ImmediateSystemContext>`，自行跨帧调用后台组的 `Update`。主组 `Complete` 覆盖主组的运动工作；应用退出或切换场景时完成后台组，再释放 Streaming 与来源。

## 同一请求的详细前段

`SceneNavigationSearch` 使用粗图定位远处目标所属 Tile，并先完成当前驻留部分的详细路线。`NavigationSearchStatus.WaitingForTiles` 与 `NavigationPath.RequiredTiles` 公开后续需求，`Corners`、`Sections` 和 `Dependencies` 同时给出可消费的详细前段。运动进度在前段内报告 `Moving`，走到末端后报告 `WaitingForTiles`。

每帧搜索入口收到当前实际运动输入。新块接入后，已有粗路线继续细化；目标块取得后，搜索从实际位置验证最终详细连接。等待期间的目标块连接采用直线成本下界，`Path.Cost` 记录已完成详细前段的实际成本；最终请求重新计算目标的局部连接。新的前段交付时重置拐点进度，角色的请求版本持续保持。`INavigationSearch.Update` 与 `NavigationSearchStep.ResetPathProgress` 是该续行入口。

`NavigationScene.CollectBoundaries` 从实际驻留的起点多边形遍历可通行邻接。块内 portal、粗图中的驻留跨块入口，以及两个已放置区域的共同边界连接共同裁剪开放区间；其余边段进入[统一避障](Navigation-Avoidance.md)。边段携带来源 Mesh 的已烘焙半径。

以下是调用顺序的例子，区域图、起点块和角色由游戏提供：

```csharp
using NavigationMeshTileSource source = block.CreateTileSource(selection);
using var streaming = new NavigationStreaming(scene, scheduler);
streaming.AddSource(regionId, source);
var interest = new NavigationInterest(actualPosition, radius: 2) { LookAheadDistance = 12 };
streaming.Interests.Add(interest);
backgroundGroup.Add(new NavigationStreamingSystem(streaming));

interest.Position = actualPosition;
interest.Path = world.Read<NavigationPathResult>(entity).Path;
backgroundGroup.Update();
NavigationStep step = navigation.Schedule(deltaTime, expansionBudget, streaming.Applied);
```

公共行为入口：[逐块运动与驻留采样](../../tests/SomeEngine.Assets.Tests/NavigationStreamingTests.cs)、[放置场景的避障边界](../../tests/SomeEngine.Navigation.Tests/NavigationSceneBoundaryTests.cs)。实现入口：[TileSource](../../src/SomeEngine.Navigation/NavigationTileSource.cs)、[Streaming](../../src/SomeEngine.Navigation/NavigationStreaming.cs)、[Scene 搜索](../../src/SomeEngine.Navigation/SceneNavigationSearch.cs)。
