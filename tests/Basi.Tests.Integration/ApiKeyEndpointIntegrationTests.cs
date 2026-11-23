using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basi.Api.Data;
using Basi.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Basi.Tests.Integration;

public class ApiKeyEndpointIntegrationTests : IClassFixture<AuthTestFactory>
{
    private readonly AuthTestFactory _factory;

    public ApiKeyEndpointIntegrationTests(AuthTestFactory factory)
    {
        _factory = factory;
        _factory.EnableTestAuth = true;
    }

    [Fact]
    public async Task GetApiKeyStatus_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        var res = await client.GetAsync("/api/auth/api-keys");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task SetApiKey_MissingAntiforgery_Returns400()
    {
        await _factory.SeedUserAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", TestAuthHandler.TestGoogleId);

        var res = await client.PostAsJsonAsync("/api/auth/api-keys", new
        {
            provider = "gemini",
            apiKey = "AIzaSyTest123"
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SetApiKey_ValidRequest_StoresKeyInRam_And_GetStatusReflectsIt()
    {
        var testGoogleId = $"google-apikey-{Guid.NewGuid():N}";
        var testEmail = $"user-{Guid.NewGuid():N}@example.com";
        await _factory.SeedUserAsync(testGoogleId, testEmail, "Key User");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", testGoogleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", testEmail);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        // 1. Initially both providers false
        var statusRes = await client.GetAsync("/api/auth/api-keys");
        Assert.Equal(HttpStatusCode.OK, statusRes.StatusCode);
        var initialStatus = await statusRes.Content.ReadFromJsonAsync<Dictionary<string, bool>>();
        Assert.NotNull(initialStatus);
        Assert.False(initialStatus["gemini"]);
        Assert.False(initialStatus["openai"]);

        // 2. Set Gemini key
        var setReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/api-keys")
        {
            Content = JsonContent.Create(new { provider = "gemini", apiKey = "AIzaSySecret123" })
        };
        setReq.Headers.Add("X-XSRF-TOKEN", token);
        var setRes = await client.SendAsync(setReq);
        Assert.Equal(HttpStatusCode.OK, setRes.StatusCode);

        // 3. Status reflects Gemini is now true, OpenAI still false (and no raw key exposed)
        statusRes = await client.GetAsync("/api/auth/api-keys");
        var updatedStatus = await statusRes.Content.ReadFromJsonAsync<Dictionary<string, bool>>();
        Assert.NotNull(updatedStatus);
        Assert.True(updatedStatus["gemini"]);
        Assert.False(updatedStatus["openai"]);

        // Ensure raw key was not leaked in response
        var rawContent = await statusRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain("AIzaSySecret123", rawContent);

        // 4. Delete Gemini key
        var delReq = new HttpRequestMessage(HttpMethod.Delete, "/api/auth/api-keys/gemini");
        delReq.Headers.Add("X-XSRF-TOKEN", token);
        var delRes = await client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.OK, delRes.StatusCode);

        // 5. Status is false again
        statusRes = await client.GetAsync("/api/auth/api-keys");
        var clearedStatus = await statusRes.Content.ReadFromJsonAsync<Dictionary<string, bool>>();
        Assert.NotNull(clearedStatus);
        Assert.False(clearedStatus["gemini"]);
    }

    [Fact]
    public async Task SetApiKey_ReQueues_WaitingForApiKey_Jobs()
    {
        var testGoogleId = $"google-requeue-{Guid.NewGuid():N}";
        var testEmail = $"user-{Guid.NewGuid():N}@example.com";
        var user = await _factory.SeedUserAsync(testGoogleId, testEmail, "Requeue User");

        // Seed a job in WaitingForApiKey
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var novel = new Novel
            {
                Id = Guid.NewGuid(),
                OwnerId = user.Id,
                Title = "Test Waiting Novel",
                SourceLanguage = "ja",
                RawContent = "Test content",
                UploadedAt = DateTime.UtcNow
            };
            var job = new TranslationJob
            {
                Id = Guid.NewGuid(),
                OwnerId = user.Id,
                NovelId = novel.Id,
                TargetLanguage = "vi",
                Provider = "gemini",
                Model = "gemini-2.5-flash",
                Status = JobStatus.WaitingForApiKey,
                ErrorMessage = "Missing API key in RAM",
                TotalSegments = 1,
                CompletedSegments = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Novels.Add(novel);
            db.TranslationJobs.Add(job);
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-UserId", testGoogleId);
        client.DefaultRequestHeaders.Add("X-Test-Email", testEmail);

        var token = await _factory.FetchAntiforgeryTokenAsync(client);

        // Submit API key
        var setReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/api-keys")
        {
            Content = JsonContent.Create(new { provider = "gemini", apiKey = "AIzaSyValidKey" })
        };
        setReq.Headers.Add("X-XSRF-TOKEN", token);
        var setRes = await client.SendAsync(setReq);
        Assert.Equal(HttpStatusCode.OK, setRes.StatusCode);

        // Verify job transitioned to Queued
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BasiDbContext>();
            var job = db.TranslationJobs.First(j => j.OwnerId == user.Id && j.Provider == "gemini");
            Assert.Equal(JobStatus.Queued, job.Status);
            Assert.Null(job.ErrorMessage);
        }
    }
}
