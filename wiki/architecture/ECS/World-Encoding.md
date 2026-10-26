# World 存档范围编码

本页记录当前实现的存档执行职责和公共 codec 能力。入口为 [WorldSerializer](../../../src/SomeEngine.ECS.Serialization/WorldSerializer.cs)、[范围编码](../../../src/SomeEngine.ECS.Serialization/WorldSerializer.Encoding.cs) 和 [IComponentCodec](../../../src/SomeEngine.ECS.Serialization/IComponentCodec.cs)。

`WorldSerializer.WriteWorld` 由协调线程取得并保持本次 World 读取期，先写身份信息，再编码实体主值，最后沿原路径写拓扑。范围工作读取已经固定的根、类型映射和稀疏成员数据，组件和 buffer 的原存储由读取期保持到全部工作完成。协调线程负责外部 Stream、结果顺序和读取期结束。

所有实际值 codec 支持并发写入且身份范围超过 256 项时，主值编码按每段最多 256 个身份索引进入 `JobSystem.ExecuteParallel`。每段持有独立的 MemoryStream、BinaryWriter 和外部引用集合，保持原实体顺序和每个实体内的类型顺序。全部范围返回后，协调线程按区段顺序写出字节并归并引用集合，再进入拓扑输出。每段直接交付自身 writer 的数组有效范围。小范围及包含串行 codec 的存档沿原循环写出。

`IComponentCodec<T>.SupportsParallelWrite` 默认返回 `false`。返回 `true` 表示 codec 的副本可以使用独立 writer 并发调用 `Write`，并保持每个值的编码结果；codec 自己持有或引用的共享数据也须满足这项保证。生成的无状态 codec 显式返回 `true`，内部 raw 纯值编码也支持并行。已有自定义 codec 保持默认值时，整个主值阶段保留原调用顺序；`ICanonicalComponentCodec<T>` 继续表达字节格式，实际并发能力从持有的 codec 读取。

公共行为入口为 [WorldBatchEncodingTests](../../../tests/SomeEngine.ECS.Serialization.Tests/WorldBatchEncodingTests.cs)：完整 World 保存、读取、实体引用和层级消费，以及共享 codec 状态下的写入顺序和外部引用集合。关系存档内容见 [Relation Serialization](Relation-Serialization.md)。
