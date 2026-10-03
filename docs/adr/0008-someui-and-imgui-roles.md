---
status: accepted
---

# SomeUI 与 ImGui 的界面职责

框架的 UI 选择固定为 SomeUI 和 ImGui。SomeUI 用于正式 UI、HUD 和未来的编辑器，让这些产品界面沿同一套界面框架演进；ImGui 用于临时调试界面。

用户在 2026-10-03 确定按界面用途划分两者职责。SomeUI 的语言与控件要求见 [整体设计约束](../SomeUI-Requirements.md)，响应式计算方式见 [C# 闭包决策](0007-reactive-computations-use-closures.md)。
