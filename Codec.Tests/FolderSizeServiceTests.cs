using Codec.Services.Storage;
using Xunit;

namespace Codec.Tests
{
    public sealed class FolderSizeServiceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"codec-size-{Guid.NewGuid():N}");

        public FolderSizeServiceTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }

        [Fact]
        public async Task BothWalks_AddEveryFileInTheTree()
        {
            string nested = Path.Combine(_root, "data", "paks");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(_root, "game.exe"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(_root, "data", "config.ini"), new byte[17]);
            File.WriteAllBytes(Path.Combine(nested, "pak0.bin"), new byte[2345]);

            FolderSizeResult result = await FolderSizeService.TryCalculateAsync(_root, TestContext.Current.CancellationToken);
            long size = await FolderSizeService.CalculateAsync(_root, TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Equal(3362, result.Size);
            Assert.Equal(3362, size);
        }
    }
}
