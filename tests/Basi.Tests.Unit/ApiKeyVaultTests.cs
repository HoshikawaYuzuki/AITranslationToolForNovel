using Basi.Module.Translation.Services;
using Xunit;

namespace Basi.Tests.Unit;

public class ApiKeyVaultTests
{
    private readonly InMemoryApiKeyVault _vault = new();

    [Fact]
    public void SetKey_And_GetKey_ReturnsStoredKey()
    {
        var userId = Guid.NewGuid();
        _vault.SetKey(userId, "gemini", "AIzaSyTest123");

        var key = _vault.GetKey(userId, "gemini");
        Assert.Equal("AIzaSyTest123", key);
    }

    [Fact]
    public void GetKey_CaseInsensitiveProvider()
    {
        var userId = Guid.NewGuid();
        _vault.SetKey(userId, "Gemini", "AIzaSyTest123");

        var key = _vault.GetKey(userId, "gemini");
        Assert.Equal("AIzaSyTest123", key);

        var keyUpper = _vault.GetKey(userId, "GEMINI");
        Assert.Equal("AIzaSyTest123", keyUpper);
    }

    [Fact]
    public void GetKey_MissingUserOrProvider_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        Assert.Null(_vault.GetKey(userId, "gemini"));

        _vault.SetKey(userId, "gemini", "test");
        Assert.Null(_vault.GetKey(userId, "openai"));
        Assert.Null(_vault.GetKey(Guid.NewGuid(), "gemini"));
    }

    [Fact]
    public void RemoveKey_RemovesStoredKey()
    {
        var userId = Guid.NewGuid();
        _vault.SetKey(userId, "openai", "sk-test456");

        var removed = _vault.RemoveKey(userId, "openai");
        Assert.True(removed);
        Assert.Null(_vault.GetKey(userId, "openai"));

        var removedAgain = _vault.RemoveKey(userId, "openai");
        Assert.False(removedAgain);
    }

    [Fact]
    public void GetKeyStatus_ReportsPresenceWithoutExposingRawKey()
    {
        var userId = Guid.NewGuid();
        _vault.SetKey(userId, "gemini", "secret_gemini_key");

        var status = _vault.GetKeyStatus(userId);
        Assert.True(status["gemini"]);
        Assert.False(status["openai"]);
    }

    [Fact]
    public void MultiUser_Isolation_Verified()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        _vault.SetKey(userA, "gemini", "key_A");
        _vault.SetKey(userB, "gemini", "key_B");

        Assert.Equal("key_A", _vault.GetKey(userA, "gemini"));
        Assert.Equal("key_B", _vault.GetKey(userB, "gemini"));
    }

    [Fact]
    public void ConcurrentAccess_ThreadSafe()
    {
        var userId = Guid.NewGuid();
        Parallel.For(0, 100, i =>
        {
            _vault.SetKey(userId, $"provider_{i % 5}", $"key_{i}");
            var status = _vault.GetKeyStatus(userId);
            Assert.NotNull(status);
        });
    }
}
