# 建造经营游戏目标

首期交付一个最小可玩的第三人称建造与模拟经营游戏。各系统按游戏实际需要贯通首批能力，再分阶段补齐已确认的设计目标。

## 场景与经营

背景为现代日本的一座没落小镇，采用制毒贩毒题材。首期活动范围为一处可改造据点及周边步行街区，据点承担搭建和生产，街区承担采购、交付与人物交流。

生产链由虚构原料、游戏配方与抽象加工步骤表达。少量街区熟人提供交易、订单和场地机会，人物关系随经营推进，经营增长改变玩家在当地的位置。

主题与氛围参考 usagi 的《地元最高！》：地方熟人关系、轻快日常与沉重处境交织。作品介绍见 [彩図社第一卷介绍](https://www.saiz.co.jp/saizhtml/bookisbn.php?i=978-4-8013-0609-7) 和 [官方作品介绍](https://jimotosaiko.com/)。

## 建造与角色

角色操作、刚体与约束交互、界面驱动玩法组成一个共同场景。玩家以地板、墙、平台等结构件搭建据点，生产设备作为完整物件放置。

动力学模拟覆盖可移动物件与设备活动部件，建筑提供静态碰撞。角色物理的首期行为与算法参考范围见 [角色物理控制决策](../../../docs/adr/0019-animation-driven-character-physics.md)。

## 交付

首次交付覆盖 Windows 与 Linux，采用 Vulkan 和 NativeAOT。

建造布置、设备、物件、库存、资金和订单等经营进度可保存并恢复。

## 工程边界

游戏工作目录为 `games/SomeGame`，与引擎共用 Git 仓库。玩法、内容、经营组件和游戏文档由该目录持有；通用物理、动画、渲染、UI 和保存能力由引擎持有。

游戏使用自己的 `Game.slnx` 和 `Game.csproj`，通过 `SomeEnginePath` 引用引擎源码。游戏、CLI 与编辑器开发能力由该游戏项目编译；标准材质、导入与资产构建来自引擎。项目持有 Shader 定义、顶层组合入口以及按实际需要添加的定制实现。

## 相关设计

- [引擎目标](../../../wiki/goals/Engine-Goals.md)
- [姿势动画规格](../../../docs/Pose-Animation-Spec.md)
- [AVBD 与长程约束决策](../../../docs/adr/0018-avbd-with-long-range-constraints.md)
- [SomeUI](../../../wiki/architecture/SomeUI.md)
- [Animation](../../../wiki/architecture/Animation.md)
