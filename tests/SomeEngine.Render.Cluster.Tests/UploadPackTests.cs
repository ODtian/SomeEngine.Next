using SomeEngine.Render.Cluster;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class UploadPackTests
{
    [Fact]
    public void TakeReturnsCopiedPayloadsAndClearsPack()
    {
        var pack = new UploadPack();
        byte[] source = [1, 2, 3, 4];

        pack.Copy(16, source);
        UploadItem item = Assert.Single(pack.Take());

        Assert.Equal(16ul, item.Offset);
        Assert.Equal(source, item.Data.ToArray());
        Assert.Equal(0, pack.Count);
    }
}
