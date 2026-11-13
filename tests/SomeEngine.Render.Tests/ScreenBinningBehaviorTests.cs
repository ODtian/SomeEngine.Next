using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class ScreenBinningBehaviorTests : IDisposable
{
    private readonly BinningTestDevice _graphics = new(DeviceFeatures.IndirectCommands);

    public void Dispose() => _graphics.Dispose();

    [Fact]
    public void RectangleExpansionVisitsEveryCellInTheVolume()
    {
        const uint cellCount = 80 * 45 * 16;
        Rectangle[] items = [new(0, 0, 0, 80, 45, 16, 80, 45)];
        using var binning = new BinningGraph(_graphics, items, cellCount, cellCount);

        binning.RenderFrame(items);

        uint[] cells = binning.Cells.Values;
        uint[] indices = binning.Indices.Values;
        Assert.Equal(cellCount * 2, (uint)cells.Length);
        for (uint cell = 0; cell < cellCount; cell++)
        {
            Assert.Equal(cell, cells[cell * 2]);
            Assert.Equal(1u, cells[cell * 2 + 1]);
            Assert.Equal(0u, indices[cells[cell * 2]]);
        }
        Assert.Equal(0u, binning.LastFrameOverflowCells);
        Assert.Equal(cellCount, binning.ExpandedCount);
        Assert.Equal(cellCount, binning.RetainedIndices);
    }

    [Fact]
    public void CellListsContainItemIndicesInTheSameOrderAcrossFrames()
    {
        Rectangle[] items =
        [
            new(0, 0, 0, 8, 4, 2, 8, 4),
            new(2, 0, 0, 4, 4, 2, 8, 4),
            new(0, 1, 1, 8, 2, 1, 8, 4),
            new(1, 1, 0, 6, 2, 2, 8, 4),
        ];
        using var binning = new BinningGraph(_graphics, items, 64, 256);
        binning.RenderFrame(items);
        uint[] firstCells = binning.Cells.Values;
        uint[] firstIndices = binning.Indices.Values;

        binning.RenderFrame(items);

        Assert.Equal(firstCells, binning.Cells.Values);
        Assert.Equal(firstIndices, binning.Indices.Values);
        for (uint cell = 0; cell < 64; cell++)
        {
            uint x = cell % 8, y = cell / 8 % 4, z = cell / 32;
            uint[] expected = items.Select((item, index) => (item, index))
                .Where(pair => x >= pair.item.X && x < pair.item.X + pair.item.Width &&
                    y >= pair.item.Y && y < pair.item.Y + pair.item.Height &&
                    z >= pair.item.Z && z < pair.item.Z + pair.item.Depth)
                .Select(pair => (uint)pair.index).ToArray();
            uint start = firstCells[cell * 2], count = firstCells[cell * 2 + 1];
            Assert.Equal(expected, firstIndices.AsSpan((int)start, (int)count).ToArray());
        }
    }

    [Fact]
    public void HalfCapacityKeepsCompleteCellsInCellOrder()
    {
        Rectangle[] items = [new(0, 0, 0, 8, 4, 2, 8, 4), new(0, 0, 0, 8, 4, 2, 8, 4)];
        using var binning = new BinningGraph(_graphics, items, 64, 64);

        binning.RenderFrame(items);

        uint[] cells = binning.Cells.Values;
        uint[] indices = binning.Indices.Values;
        for (int cell = 0; cell < 32; cell++)
        {
            Assert.Equal(2u, cells[cell * 2 + 1]);
            Assert.Equal(new uint[] { 0, 1 }, indices.AsSpan((int)cells[cell * 2], 2).ToArray());
        }
        for (int cell = 32; cell < 64; cell++) Assert.Equal(0u, cells[cell * 2 + 1]);
        Assert.Equal(32u, binning.LastFrameOverflowCells);
        Assert.Equal(128u, binning.RequiredListLength);
        Assert.Equal(64u, binning.RetainedIndices);
    }

    [Fact]
    public void DiscardedCapacityEditsKeepTheSubmittedCellLists()
    {
        Rectangle[] items = [new(0, 0, 0, 8, 4, 2, 8, 4), new(0, 0, 0, 8, 4, 2, 8, 4)];
        using var binning = new BinningGraph(_graphics, items, 64, 64);
        binning.RenderFrame(items);
        uint[] submittedCells = binning.Cells.Values;
        uint[] submittedIndices = binning.Indices.Values;

        binning.DiscardListCapacityEdit(128);
        binning.RenderFrame(items);

        Assert.Equal(64u, binning.ListCapacity);
        Assert.Equal(submittedCells, binning.Cells.Values);
        Assert.Equal(submittedIndices, binning.Indices.Values);
        for (int cell = 0; cell < 32; cell++)
        {
            Assert.Equal(2u, binning.Cells.Values[cell * 2 + 1]);
            Assert.Equal(new uint[] { 0, 1 },
                binning.Indices.Values.AsSpan((int)binning.Cells.Values[cell * 2], 2).ToArray());
        }
        for (int cell = 32; cell < 64; cell++) Assert.Equal(0u, binning.Cells.Values[cell * 2 + 1]);
        Assert.Equal(32u, binning.LastFrameOverflowCells);
        Assert.Equal(128u, binning.RequiredListLength);
    }

    [Fact]
    public async Task ReplacingShaderLayoutsPreservesTheGraphResourcesAndCellLists()
    {
        Rectangle[] items = [new(0, 0, 0, 4, 1, 1, 4, 1), new(1, 0, 0, 2, 1, 1, 4, 1)];
        using var binning = new BinningGraph(_graphics, items, 4, 8);
        binning.RenderFrame(items);
        uint[] firstCells = binning.Cells.Values;
        uint[] firstIndices = binning.Indices.Values;
        ScreenBinningResources original = binning.Binning;
        ulong version = binning.Graph.StructureVersion;
        string source = BinningGraph.ShaderSource
            .Replace("#include \"screen_binning.slang\"", "")
            .Replace("StructuredBuffer<uint> RectangleUnused;", "")
            .Replace("struct RectangleSource", "#include \"screen_binning.slang\"\nstruct RectangleSource");
        Shader replacement = await binning.Shaders.CreateAsync("Reordered screen binning inputs", "reordered_screen_items.slang", source);
        ScreenBinningResources replaced;
        RenderGraphEdit edit = binning.Graph.BeginEdit();
        try
        {
            replaced = ScreenBinningPasses.ReplaceShader(ref edit, original, _graphics.Pipelines, binning.SourceShader,
                replacement, (name, _) => name == "RectangleItems"
                    ? GraphParameterResourceBinding.ReadOnlyBufferDescriptor(binning.ItemsView) : null);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        binning.RenderFrame(items);

        Assert.Equal(version + 1u, binning.Graph.StructureVersion);
        Assert.Equal(original.CompletePass, replaced.CompletePass);
        Assert.Equal(original.Cells, replaced.Cells);
        Assert.Equal(original.Indices, replaced.Indices);
        Assert.Equal(original.Diagnostics, replaced.Diagnostics);
        Assert.Equal(firstCells, binning.Cells.Values);
        Assert.Equal(firstIndices, binning.Indices.Values);
        Assert.Equal(new uint[] { 0, 1, 1, 2, 3, 2, 5, 1 }, binning.Cells.Values);
        Assert.Equal(new uint[] { 0, 0, 1, 0, 1, 0 }, binning.Indices.Values.AsSpan(0, 6).ToArray());
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rectangle(uint X, uint Y, uint Z, uint Width, uint Height, uint Depth,
        uint GridWidth, uint GridHeight);

    private sealed class UIntReadback : IGraphReadbackReceiver
    {
        internal uint[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    private sealed class BinningGraph : IDisposable, IGraphReadbackReceiver
    {
        private readonly RenderGraph.RenderGraph _graph;
        private readonly ShaderLibrary _shaders;
        private readonly GraphBufferInput _items;
        private readonly GraphBufferSrvId _itemsView;
        private readonly IndirectCommandLayout _dispatchLayout;
        private readonly uint _itemCapacity, _cellCount;
        private readonly GraphReadbackId _cellsReadback;
        private readonly GraphReadbackId _indicesReadback;
        internal readonly UIntReadback Cells = new();
        internal readonly UIntReadback Indices = new();
        internal readonly ScreenBinningResources Binning;
        internal RenderGraph.RenderGraph Graph => _graph;
        internal ShaderLibrary Shaders => _shaders;
        internal Shader SourceShader { get; }
        internal GraphBufferSrvId ItemsView => _itemsView;
        internal uint LastFrameOverflowCells { get; private set; }
        internal uint RequiredListLength { get; private set; }
        internal uint ExpandedCount { get; private set; }
        internal uint RetainedIndices { get; private set; }
        internal uint ListCapacity { get; private set; }

        internal BinningGraph(BinningTestDevice graphics, Rectangle[] items, uint cellCount, uint listCapacity)
        {
            _itemCapacity = (uint)items.Length;
            _cellCount = cellCount;
            IGlobalSession slang = Shader.CreateSlangGlobalSession();
            SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
            settings.SearchPaths = [ShaderDirectory()];
            _shaders = new ShaderLibrary(slang, settings);
            Shader shader = _shaders.CreateAsync("Screen binning rectangles", "screen_binning_rectangles.slang", ShaderSource)
                .AsTask().GetAwaiter().GetResult();
            SourceShader = shader;
            _graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
                [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
            _dispatchLayout = graphics.Backend.CreateIndirectCommandLayout(graphics.Device, new(
                [new IndirectArgumentDesc(IndirectArgumentType.Dispatch)], 12, label: "Rectangle binning dispatch"));
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                GraphBufferInput itemBuffer = edit.CreateBufferInput(BufferUsages.ShaderRead);
                GraphBufferSrvId itemsView = edit.CreateBufferSrv(itemBuffer, structureStride: 32);
                ScreenBinningResources binning = ScreenBinningPasses.Add(ref edit, graphics.Pipelines, shader, shader, _dispatchLayout,
                    (uint)items.Length, cellCount, listCapacity,
                    (name, _) => name == "RectangleItems"
                        ? GraphParameterResourceBinding.ReadOnlyBufferDescriptor(itemsView)
                        : null, [itemsView], this);
                GraphReadbackId cellsReadback = edit.AddReadback(binning.Cells, new(0, cellCount * 8), Cells, "Read screen cells");
                GraphReadbackId indicesReadback = edit.AddReadback(binning.Indices, new(0, listCapacity * 4), Indices, "Read screen items");
                edit.OrderAfter(cellsReadback, binning.CompletePass);
                edit.OrderAfter(indicesReadback, binning.CompletePass);
                edit.Commit();
                Binning = binning;
                _items = itemBuffer;
                _itemsView = itemsView;
                _cellsReadback = cellsReadback;
                _indicesReadback = indicesReadback;
            }
            finally { edit.Dispose(); }
        }

        internal void RenderFrame(Rectangle[] items)
        {
            ulong identity;
            RenderGraphFrame frame = _graph.BeginFrame();
            try
            {
                frame.SetBufferInput(_items, frame.Upload<Rectangle>(items.AsSpan(),
                    BufferUsages.ShaderRead, "Screen binning rectangles"));
                ScreenBinningPasses.SetFrameParameters(ref frame, Binning, (uint)items.Length, _itemCapacity, _cellCount);
                identity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[_graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, _graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        }

        internal void DiscardListCapacityEdit(uint listCapacity)
        {
            RenderGraphEdit edit = _graph.BeginEdit();
            try { ScreenBinningPasses.SetListCapacity(ref edit, Binning, listCapacity); }
            finally { edit.Dispose(); }
        }

        public void Dispose()
        {
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                edit.Remove(_cellsReadback);
                edit.Remove(_indicesReadback);
                ScreenBinningPasses.Remove(ref edit, Binning);
                edit.Remove(_itemsView);
                edit.Commit();
            }
            finally { edit.Dispose(); }
            _graph.RetireAfterSubmittedFrames(_dispatchLayout);
            _graph.Dispose();
            _shaders.Dispose();
        }

        void IGraphReadbackReceiver.OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(bytes);
            LastFrameOverflowCells = values[0];
            RequiredListLength = values[1];
            ExpandedCount = values[2];
            RetainedIndices = values[4];
            ListCapacity = values[3];
        }

        private static string ShaderDirectory()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "screen_binning.slang")))
                directory = directory.Parent!;
            return Path.Combine(directory.FullName, "assets", "Shaders");
        }

        internal const string ShaderSource = """
            #include "screen_binning.slang"
            StructuredBuffer<uint> RectangleUnused;
            struct RectangleItem
            {
                uint3 Minimum;
                uint3 Size;
                uint Width;
                uint Height;
            };
            StructuredBuffer<RectangleItem> RectangleItems;
            struct RectangleSource : IScreenBinningItem
            {
                static ScreenBinningBounds GetBounds(uint itemIndex)
                {
                    RectangleItem item = RectangleItems[itemIndex];
                    ScreenBinningBounds bounds;
                    bounds.Minimum = item.Minimum;
                    bounds.Size = item.Size;
                    return bounds;
                }
                static bool Intersects(uint itemIndex, uint3 cell) { return true; }
                static uint GetCellIndex(uint itemIndex, uint3 cell)
                {
                    RectangleItem item = RectangleItems[itemIndex];
                    return (cell.z * item.Height + cell.y) * item.Width + cell.x;
                }
                static uint GetItemIndex(uint itemIndex) { return itemIndex; }
            };
            [shader("compute")][numthreads(256,1,1)]
            void CountScreenItems(uint3 id:SV_DispatchThreadID)
            { ScreenBinningCount<RectangleSource>(ScreenBinningThreadIndex(id)); }
            [shader("compute")][numthreads(256,1,1)]
            void ExpandScreenItems(uint3 id:SV_DispatchThreadID)
            { ScreenBinningExpand<RectangleSource>(ScreenBinningThreadIndex(id)); }
            [shader("compute")][numthreads(256,1,1)]
            void FillScreenItems(uint3 id:SV_DispatchThreadID)
            { ScreenBinningFill<RectangleSource>(ScreenBinningThreadIndex(id)); }
            """;
    }
}
