# Accessibility

## 数据与公开入口

`SomeEngine.Elements` 在元素和控件节点上写入角色与名称，供应用通过
`World.Read<AccessibleRole>` 和 `World.Read<AccessibleName>` 读取。
两种数据都是表格组件；名称以字符串值比较，沿 `Scope.Write` 在计算结束时提交。

```csharp
public enum Role : byte
{
    Group, Text, Image, Button, Toggle, Slider, TextField, Dialog, Heading, List
}

public record struct AccessibleRole : IComponent { public Role Value; }
public record struct AccessibleName : IComponent { public string Value; }

public static Entity Accessible(this Entity node, Role role, string name);
```

每种内置元素和控件在首次构建后都具有这两个组件。默认数据如下：

| 视图 | 角色 | 名称 |
| --- | --- | --- |
| `Text` | `Role.Text` | 解析后的显示字符串，包含本地化与格式化结果 |
| `Image` | `Role.Image` | 空字符串 |
| `Button` | `Role.Button` | `Label` |
| `Toggle` | `Role.Toggle` | 空字符串 |
| `Slider` | `Role.Slider` | 空字符串 |
| `TextField` | `Role.TextField` | `Placeholder` |
| `Dialog` | `Role.Dialog` | 空字符串 |
| `DialogTitle` | `Role.Heading` | `Text` |
| `DialogActions` | `Role.Group` | 空字符串 |
| `ScrollView` | `Role.Group` | 空字符串 |
| `VirtualList` | `Role.List` | 空字符串 |

Text 的名称跟随已解析的显示字符串更新；Button 与 DialogTitle 的名称跟随
参数更新。TextField 的名称使用占位提示，跟随 Placeholder 更新；编辑文字、
光标与输入法组合继续从公开 `EditableText` 读取。Toggle 的开关值与 Slider 的
数值、范围继续由它们的参数和公开行为数据表达。Image 的描述与控件的业务标签
由应用的组合视图明确提供。

Dialog 的标题与操作区域分别是独立节点，角色跟随各部件本身；调用方可以按
Composition 的规则拼装部件。`TitledDialog` 采用相同的 Dialog 与 DialogTitle。

## 用户组件

`.Accessible(role, name)` 返回传入实体，写入两种公开组件。
用户组件用它声明自己的角色与业务标签；其写入与其它修饰使用相同的计算提交规则。

```csharp
public record struct InventoryButton(string Label) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        Scope.Entity.Accessible(Role.Button, Label);
        PressableFunctions.Pressable(Scope.Entity);
        TextFunctions.Text(Label);
        return default;
    }
}
```

内置节点的默认数据由该视图的构建或内容解析计算写入；用户节点的数据由声明
`.Accessible` 的计算写入。每个节点的数据由其组件保存，写入职责见 Data Writers。
空字符串表示由应用提供业务标签的节点。角色使用枚举中已定义的值，名称使用有效
字符串；使用错误由 Core `DevelopmentCheck` 报告，检查调用随 shipping 编译移除。
修饰使用所需的构建或计算作用域由 `Scope.Write` 的同一开发检查保证。

平台无障碍接口（例如 Windows UIA）作为后续独立系统消费这些数据。

## 参考取舍

此前独立 SomeUI 的 `src/SomeUI.Semantics` 提供数值、编辑和滚动行为的纯数据计算。
本页的角色、名称与 ECS 修饰形状沿用 #54 和当前 SomeUI 视图模型，数据由
`SomeEngine.Elements` 的公开组件持有。

## Relations
[[architecture/someui/Composition|Composition]] · [[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Testing|Testing]]
