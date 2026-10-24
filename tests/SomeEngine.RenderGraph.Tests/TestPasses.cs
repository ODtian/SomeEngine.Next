namespace SomeEngine.RenderGraph.Tests;

internal delegate void TestPassDeclaration(ref PassDefinition access);
internal delegate void TestCopyCommands(ref CopyPassCommandScope commands);
internal delegate void TestComputeCommands(ref ComputePassCommandScope commands);
internal delegate void TestGeneralCommands(ref GeneralPassCommandScope commands);

// Only adapt callbacks; accesses, commands and pass options stay visible in each test.
internal static class TestPasses
{
    internal static GraphPassId DeclareCopyPass(this RenderGraphEdit edit, string label,
        PassQueueSelection queue, TestPassDeclaration? declare = null,
        TestCopyCommands? record = null, PassOptions options = default) =>
        edit.AddCopyPass<byte, byte>(label, queue, 0, options,
            (ref PassDefinition access, ref byte _) => declare?.Invoke(ref access),
            (ref CopyPassCommandScope commands, in byte _, in byte _) => record?.Invoke(ref commands));

    internal static GraphPassId DeclareCopyPass(this RenderGraphFrame frame, string label,
        PassQueueSelection queue, TestPassDeclaration? declare = null,
        TestCopyCommands? record = null, PassOptions options = default) =>
        frame.AddCopyPass(label, queue, (byte)0, options,
            (ref PassDefinition access, ref byte _) => declare?.Invoke(ref access),
            (ref CopyPassCommandScope commands, in byte _) => record?.Invoke(ref commands));

    internal static GraphPassId DeclareComputePass(this RenderGraphEdit edit, string label,
        PassQueueSelection queue, TestPassDeclaration? declare = null,
        TestComputeCommands? record = null, PassOptions options = default) =>
        edit.AddComputePass<byte, byte>(label, queue, 0, options,
            (ref PassDefinition access, ref byte _) => declare?.Invoke(ref access),
            (ref ComputePassCommandScope commands, in byte _, in byte _) => record?.Invoke(ref commands));

    internal static GraphPassId DeclareComputePass(this RenderGraphFrame frame, string label,
        PassQueueSelection queue, TestPassDeclaration? declare = null,
        TestComputeCommands? record = null, PassOptions options = default) =>
        frame.AddComputePass(label, queue, (byte)0, options,
            (ref PassDefinition access, ref byte _) => declare?.Invoke(ref access),
            (ref ComputePassCommandScope commands, in byte _) => record?.Invoke(ref commands));

    internal static GraphPassId DeclareGeneralPass(this RenderGraphEdit edit, string label,
        PassQueueSelection queue, TestPassDeclaration? declare = null,
        TestGeneralCommands? record = null, PassOptions options = default) =>
        edit.AddGeneralPass<byte, byte>(label, queue, 0, options,
            (ref PassDefinition access, ref byte _) => declare?.Invoke(ref access),
            (ref GeneralPassCommandScope commands, in byte _, in byte _) => record?.Invoke(ref commands));

    internal static GraphPassId DeclareGeneralPass(this RenderGraphFrame frame, string label,
        PassQueueSelection queue, TestPassDeclaration? declare = null,
        TestGeneralCommands? record = null, PassOptions options = default) =>
        frame.AddGeneralPass(label, queue, (byte)0, options,
            (ref PassDefinition access, ref byte _) => declare?.Invoke(ref access),
            (ref GeneralPassCommandScope commands, in byte _) => record?.Invoke(ref commands));

    internal static GraphPassId DeclareBufferCopy(this RenderGraphEdit edit, string label,
        PassQueueSelection queue, GraphBufferSlice source, GraphBufferSlice destination,
        PassOptions options = default) =>
        edit.DeclareCopyPass(label, queue,
            (ref PassDefinition access) =>
            {
                access.Read(source.Buffer, source.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                access.Write(destination.Buffer, destination.Range, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            (ref CopyPassCommandScope commands) => commands.CopyBuffer(new(
                commands.GetBuffer(source.Buffer), source.Range.Offset,
                commands.GetBuffer(destination.Buffer), destination.Range.Offset, source.Range.Size)), options);

    internal static GraphPassId DeclareBufferCopy(this RenderGraphFrame frame, string label,
        PassQueueSelection queue, GraphBufferSlice source, GraphBufferSlice destination,
        PassOptions options = default) =>
        frame.DeclareCopyPass(label, queue,
            (ref PassDefinition access) =>
            {
                access.Read(source.Buffer, source.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                access.Write(destination.Buffer, destination.Range, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            (ref CopyPassCommandScope commands) => commands.CopyBuffer(new(
                commands.GetBuffer(source.Buffer), source.Range.Offset,
                commands.GetBuffer(destination.Buffer), destination.Range.Offset, source.Range.Size)), options);
}
