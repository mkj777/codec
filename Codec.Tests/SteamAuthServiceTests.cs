using Codec.Services.Steam;
using Xunit;

namespace Codec.Tests;

public sealed class SteamAuthServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codec-steam-auth-" + Guid.NewGuid().ToString("N"));

    public SteamAuthServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string TokenPath => Path.Combine(_directory, "steam-token.dat");

    [Fact]
    public void HasStoredToken_AsksTheDiskOnceThenKeepsTheAnswer()
    {
        File.WriteAllBytes(TokenPath, [1, 2, 3]);
        var service = new SteamAuthService(TokenPath);

        Assert.True(service.HasStoredToken);

        // Only the service writes the token, so a change behind its back is not looked for.
        File.Delete(TokenPath);
        Assert.True(service.HasStoredToken);
    }

    [Fact]
    public async Task HasStoredToken_FollowsDelete()
    {
        File.WriteAllBytes(TokenPath, [1, 2, 3]);
        var service = new SteamAuthService(TokenPath);
        Assert.True(service.HasStoredToken);

        await service.DeleteTokenAsync();

        Assert.False(service.HasStoredToken);
        Assert.False(File.Exists(TokenPath));
    }

    [Fact]
    public void HasStoredToken_IsFalseWithoutAFile()
    {
        var service = new SteamAuthService(TokenPath);

        Assert.False(service.HasStoredToken);
    }
}
