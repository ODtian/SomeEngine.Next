# Material 继承的公共 API 复现

Release 生产程序集和实际 source generator；无内部访问。分别设置 Roughness=0.25 和 Coating=0.75，结果两个属性均为 0.75，且只有一个槽。

```xml
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
 <ItemGroup>
  <ProjectReference Include="../src/SomeEngine.Assets/SomeEngine.Assets.csproj" />
  <ProjectReference Include="../src/SomeEngine.Generators/SomeEngine.Generators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
 </ItemGroup>
</Project>

```

```csharp
using SomeEngine.Assets.Schema;

var material = new CoatedMaterial();
material.Roughness = 0.25f;
material.Coating = 0.75f;
Console.WriteLine($"CHECK generated inherited properties: Roughness={material.Roughness}; Coating={material.Coating}; SlotCount={material.SlotCount}");

public partial class SurfaceMaterial : Material
{
    public partial float Roughness { get; set; }
}

public sealed partial class CoatedMaterial : SurfaceMaterial
{
    public partial float Coating { get; set; }
}

```
