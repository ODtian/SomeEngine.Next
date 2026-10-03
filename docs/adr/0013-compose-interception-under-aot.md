---
status: accepted
---

# 只面向 NativeAOT，Compose 写法使用拦截器

SomeUI 只考虑 NativeAOT。源生成器用 C# 拦截器为所有构建体内产出节点的调用提供调用位置常量，节点身份因此在所有写法中统一为调用位置。Compose 写法的 `[Composable]` 函数另由生成器生成视图 struct，调用点被改写为视图声明。

拦截器只能替换调用点，所以无法拦截的用法由分析器报告为编译错误：方法组或委托使用、位于其它生成器产出代码中的调用、未开启 `InterceptorsNamespaces` 的项目等。编译通过即保证每个调用都已被拦截。

AOT 下不使用反射、`MakeGenericType`、`Activator` 或表达式树编译；视图类型的序列化注册由生成器完成。

用户在 2026-10-03 确定。
