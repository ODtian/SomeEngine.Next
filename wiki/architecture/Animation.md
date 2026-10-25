# Animation

本页只负责导航。动画是引擎级的库，由一个通用核心加两种数据形态组成（ADR 0014）。

## 核心
- [[architecture/animation/Timeline|Timeline]]
- [[architecture/animation/Value-Ops|Value Ops and Curves]]
- [[architecture/animation/Animation-Layers|Animation Layers]]
- [[architecture/animation/Animation-Graph|Animation Graph]]
- [[architecture/animation/Animation-Extension|Animation Extension]]

## 数据形态
- [[architecture/animation/Property-Animation|Property Animation]]
- [[architecture/animation/Pose-Animation|Pose Animation]]

## 使用者
- [[architecture/someui/Animation|SomeUI Animation]]：`.Animate` 与 FLIP
- [[architecture/animation/Crowd-Example|Crowd Example]]：统一场景平台中的 1000 个独立 GPU 角色
