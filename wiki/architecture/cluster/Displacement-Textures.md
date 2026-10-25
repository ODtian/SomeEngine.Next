# Displacement Range Textures

本页维护已实现的公开范围纹理构建与采样链。源码入口为 [DisplacementBoundsTexture](../../../src/SomeEngine.Assets.Importers/Importers/DisplacementBoundsTexture.cs)、[TextureBlock](../../../src/SomeEngine.Assets/Schema/TextureBlock.cs) 和 [texture_displacement.slang](../../../assets/Shaders/texture_displacement.slang)。

`DisplacementBoundsTexture.Create` 接收宽高和高度样本，返回普通 TextureAsset，并通过 `sourceRange` 返回整个来源的 Min/Max。纹理采用 R32G32Float：基础层的每个 texel 保存对应样本的相同 Min/Max，后续层保存覆盖来源范围的 Min/Max。奇数尺寸继续按归一化纹理范围确定覆盖区间，保留最后一行和最后一列。

构建调用固定来源样本的有效读取范围，通过 `JobSystem.ExecuteParallel` 按 4096 个样本分区写入基础 texel 和各分区范围，再由调用方归并整体范围。每层 Mip 以输出行分派独立工作，工作只读取完成的上一层并写入自己的行；同步批次返回后才将该层交给下一层。TextureBlock 的像素和 Mip 描述由构建调用集中安装，返回资产时所有层已经形成。

资产保存重开后，`TextureBlock.CreateAsync(firstMip: 0)` 可取得全部 Mip 的公开 Texture 像素；按需取得其他层的入口为 `LoadMipAsync`。用户 Shader 将高度纹理、范围纹理、采样器和来源整体范围传给 `MakeTextureDisplacement`。`position` 求位移后的位置，`patchBounds` 消费同一范围纹理形成曲面包围；RHI 绑定按该 Shader 的直接 Slang 参数布局提供采样纹理 view 与 Sampler。

公开检查分别由 [DisplacementBoundsTextureTests](../../../tests/SomeEngine.Assets.Tests/Assets/DisplacementBoundsTextureTests.cs) 的逐层像素、整体范围与保存重开，以及 [TextureDisplacementTests.BoundsTextureDisplacementDrawsTheSamePixelsAsSourceVertices](../../../tests/SomeEngine.Graphics.Direct3D12.Tests/TextureDisplacementTests.cs) 的实际 Draw 表达。后者把公开资产产物用于 GPU 位移和包围计算，与同一高度样本生成的 CPU 顶点进行逐像素比较。
