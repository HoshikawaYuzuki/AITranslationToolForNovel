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

    [Fact]
    public void SetJobKey_WithOwner_EnforcesOwnership()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var jobA = Guid.NewGuid();

        _vault.SetJobKey(userA, jobA, "secret_job_key");

        // Owner can access
        Assert.Equal("secret_job_key", _vault.GetJobKey(userA, jobA));
        Assert.Equal("secret_job_key", _vault.GetJobKey(jobA));

        // Different user cannot access
        Assert.Null(_vault.GetJobKey(userB, jobA));
    }

    [Fact]
    public void ClearAll_RemovesUserKeysAndJobKeys_PreservesOtherUsersJobKeys()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();

        _vault.SetKey(userA, "gemini", "user_A_key");
        _vault.SetJobKey(userA, jobA, "job_A_key");

        _vault.SetKey(userB, "gemini", "user_B_key");
        _vault.SetJobKey(userB, jobB, "job_B_key");

        // Probe before ClearAll
        Assert.True(_vault.HasKey(userA, "gemini"));
        Assert.True(_vault.HasJobKey(jobA));
        Assert.True(_vault.HasJobKey(jobB));

        // ClearAll for User A
        _vault.ClearAll(userA);

        // User A's keys (both user-level and job-level) are removed!
        Assert.False(_vault.HasKey(userA, "gemini"));
        Assert.False(_vault.HasJobKey(jobA));
        Assert.Null(_vault.GetJobKey(jobA));

        // User B's keys are completely preserved!
        Assert.True(_vault.HasKey(userB, "gemini"));
        Assert.True(_vault.HasJobKey(jobB));
        Assert.Equal("job_B_key", _vault.GetJobKey(jobB));
    }

    [Fact]
    public void RemoveJobKeysForUser_RemovesOnlyTargetUserJobKeys()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var job1 = Guid.NewGuid();
        var job2 = Guid.NewGuid();
        var jobB = Guid.NewGuid();

        _vault.SetJobKey(userA, job1, "key1");
        _vault.SetJobKey(userA, job2, "key2");
        _vault.SetJobKey(userB, jobB, "keyB");

        _vault.RemoveJobKeysForUser(userA);

        Assert.False(_vault.HasJobKey(job1));
        Assert.False(_vault.HasJobKey(job2));
        Assert.True(_vault.HasJobKey(jobB));
    }
}
