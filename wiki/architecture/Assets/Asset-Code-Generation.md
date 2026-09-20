# 资产声明与代码生成

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

具体数据以一个类型声明为来源。字段访问、资产引用表达、浅复制、依赖枚举和编码连接中的重复知识由生成器产生。对象关系见 [[Asset-Objects]]，共同流程的归属见 [[Asset-Responsibilities]]。

## 声明到生成物

当前普通资产使用 `[Asset(".xxx.asset")]` 标明文件后缀，可用 `SchemaName` 指定编码类型名称。`[AssetReference(typeof(T))]` 标识声明中确实代表资产引用的数据。标记在编译期参与代码生成，不意味着运行时扫描属性。

| 来源 | 生成或连接的内容 |
| --- | --- |
| 类型的具体属性 | `.Asset` 的具体属性与私有字段存储 |
| 资产引用声明 | 当前引用枚举，或具体材质中的 ID / 对象转换 |
| 字段类型 | 当前节点的直接浅复制代码 |
| 二进制字段声明 | 内部编码数据、根类型描述及编码代码 |
| 类型已有的分块业务函数 | 保存与读取时的直接调用 |
| 应用对象的创建入口 | 具体 `.Asset` 到应用对象的显式衔接 |

没有特殊业务需求时，不手写另一套与声明重复的 `XXData`。内部生成数据可以服务编码和应用对象字段存储，但不是另一套公开编辑模型，也不另行维护公共资产名称或版本。

## 直接代码的边界

编译期已经知道字段类型、字段位置、复制规则和编码目标，就生成直接调用与赋值。不能注册 `getId`、`setName`、`copyFields`、`createWriter` 等成组 `Func` / `Action`；也不能换成逐字段接口分派、反射或运行时操作表。

不在每次创建或编码时比较内部类型标识，以补救生成器本应保证的对应关系。输入文件的格式识别和校验是另一回事，见 [[Asset-Validation]]。

材质文件确实需要在输入边界识别具体材质类型。当前材质实现为此登记生成的具体类型构造入口；这不授权将已知字段访问、复制和编码改成动态操作表。材质家族特例见 [[Asset-Materials]]。

## 生成流程与类型特例

当前 `BinaryContractGenerator` 先接入资产字段声明生成，使同次生成中的编码器能看到生成的数据定义；`AssetGenerator` 产生具体资产入口。该衔接是构建期工作，不是加载时重新推断模型。

`MaterialGen` 从具体材质声明生成应用属性、对应资产属性及转换。World 则复用已有 ECS 类型与 codec 注册，不给每个 World 再生成固定场景字段。两种特例分别见 [[Asset-Materials]] 和 [[Asset-Worlds]]。

生成器负责数据节点的浅复制，但嵌套写入的公开入口尚未确认。不能用“生成器可以生成”作为实现新访问对象或不可变集合模型的授权，详见 [[Asset-Open-Questions]]。

## 验证生成结果

验证使用外部消费程序集声明类型、编译、调用公开 API、保存重开和运行 Native AOT 产物。测试不得断言生成代码字符串、私有字段布局或某段具体实现的拼写。测试原则见 [[Asset-Validation]]，已有结果见 [[Asset-Verification-2026-09-10]]。

代码入口：[声明标记](../../../src/SomeEngine.Assets/AssetAttribute.cs)、[AssetGenerator](../../../src/SomeEngine.Generators/AssetGenerator.cs)、[编码生成衔接](../../../src/SomeEngine.Generators/BinaryContractGenerator.cs)、[MaterialGen](../../../src/SomeEngine.Generators/MaterialGen.cs)。

返回 [[Asset-Files]]；关联 [[Asset-Sharing-and-Writes]]、[[Asset-Identity-and-Dependencies]]。
