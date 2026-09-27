# 独立 Curve 资产消费样例

`SomeEngine.Curves` 定义普通 `Point`、`Curve` 和 `CurveSamples`。它只引用通用序列化库以及编译期生成器，不引用 Assets。`SomeEngine.Curves.Assets` 通过相同的 Point 声明定义可编辑资产。

CurveAsset 可以创建 Curve 和 CurveSamples；ConstantCurveAsset 也可以创建 Curve。两种资产没有使用对象字段镜像，也不要求使用对象继承资产基类。

```csharp
using SomeEngine.Examples.Curves;
using SomeEngine.Serialization.Containers;

using var asset = new CurveAsset { Name = "speed", Points = [new(0, 1), new(1, 5)] };
using var branch = asset.Duplicate();       // 同一个 ID，独立编辑分支
using var independent = asset.CreateCopy(); // 新资产 ID
branch.Points[1] = new Point(1, 9);

branch.Points.Write(0, branch.Points.Count, static (_, points) =>
{
    for (int i = 0; i < points.Length; i++)
        points[i] = points[i] with { Y = points[i].Y * 2 };
});

using var read = asset.Points.Retain(0, 2);
await Task.Yield();
// read 保留所选页，可跨 await；Dispose 后不得继续使用取得的 Span / Memory / Sequence。
Point first = read.Sequence.FirstSpan[0];

using Curve curve = asset.CreateCurve();
branch.Update(curve); // 更新同一个使用对象
asset.Dispose();
float speed = curve.Evaluate(0.5f); // 使用对象已持有数据，不再打开来源
```

批量写回调同步执行，可以分别收到多个页的 Span；回调期间不允许再次读取、复制、释放同一资产分支，也不允许将可写 Span / ref 保存到回调外或跨 await。需要异步读取时，先取得独立 PageRange，并在使用结束后释放。`PageCollections` 扩展只适用于页集合；普通 List 保持自己的集合 API。

```csharp
await branch.SaveSourceAsync("speed.curve");
using var source = await CurveAsset.OpenSourceAsync("speed.curve");
await source.SaveAsync("speed.curve.asset");
using var cooked = await CurveAsset.OpenAsync("speed.curve.asset");
```

这里 `.curve` 是人工编辑来源，`.curve.asset` 是由它构建的派生产物。产物可以删除后由来源重建。修改来源不自动修改已经创建的 Curve，调用 Update 才应用新内容。外部消费测试与 Assets.AotSmoke 编译和执行这些入口。
