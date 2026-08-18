using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ObsidianSync.Server.Tests;

public sealed class SyncApiTests : IClassFixture<SyncApplicationFactory>
{
    private readonly SyncApplicationFactory _factory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public SyncApiTests(SyncApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RegistrationRejectsRequestsWithoutTheServerRegistrationKey()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new { userName = "blocked-user", password = "correct horse battery staple" })
        };
        request.Headers.Add("X-Registration-Key", "wrong-key");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(_json);
        Assert.Equal("registration_forbidden", error!.Error);
    }

    [Fact]
    public async Task BasicSyncModificationDeleteRenameAndHistoryWork()
    {
        using var client = await CreateAuthenticatedClientAsync("basic-user");
        var vaultId = await CreateVaultAsync(client, "Personal");
        var deviceId = await CreateDeviceAsync(client, "Laptop");

        var first = await UploadAsync(client, vaultId, deviceId, "note-1", "Notes/Chili.md", 0, "one");
        Assert.Equal("accepted", first.Status);
        Assert.Equal(1, first.Revision);

        var changes = await client.GetFromJsonAsync<ChangesResponse>($"/api/vaults/{vaultId}/changes?after=0", _json);
        Assert.NotNull(changes);
        Assert.Single(changes!.Changes);
        Assert.Equal("upsert", changes.Changes[0].Operation);

        var second = await UploadAsync(client, vaultId, deviceId, "edit-1", "Notes/Chili.md", first.Revision, "two");
        Assert.Equal(2, second.Revision);

        var renamed = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/sync/rename", new
        {
            deviceId,
            operationId = "rename-1",
            oldPath = "Notes/Chili.md",
            path = "Recipes/Chili.md",
            baseFileRevision = second.Revision
        });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var renamedResult = await renamed.Content.ReadFromJsonAsync<SyncMutationResponse>(_json);
        Assert.NotNull(renamedResult);
        Assert.Equal("rename", (await client.GetFromJsonAsync<ChangesResponse>($"/api/vaults/{vaultId}/changes?after=2", _json))!.Changes[0].Operation);

        var deleted = await client.PostAsJsonAsync($"/api/vaults/{vaultId}/sync/delete", new
        {
            deviceId,
            operationId = "delete-1",
            path = "Recipes/Chili.md",
            baseFileRevision = renamedResult!.Revision
        });
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var history = await client.GetFromJsonAsync<List<HistoryDto>>($"/api/vaults/{vaultId}/files/{first.FileId}/history", _json);
        Assert.NotNull(history);
        Assert.Equal(4, history!.Count);
        Assert.Contains(history, item => item.Operation == "delete");

        var oldContent = await client.GetByteArrayAsync($"/api/vaults/{vaultId}/files/{first.FileId}/content?revision=2");
        Assert.Equal("two", System.Text.Encoding.UTF8.GetString(oldContent));

        var afterDelete = await client.GetFromJsonAsync<ChangesResponse>($"/api/vaults/{vaultId}/changes?after=3", _json);
        Assert.NotNull(afterDelete);
        Assert.Equal("delete", afterDelete!.Changes.Single().Operation);
    }

    [Fact]
    public async Task StaleUploadCreatesConflictCopyAndRetryIsIdempotent()
    {
        using var client = await CreateAuthenticatedClientAsync("conflict-user");
        var vaultId = await CreateVaultAsync(client, "Conflict Vault");
        var firstDevice = await CreateDeviceAsync(client, "Desktop");
        var secondDevice = await CreateDeviceAsync(client, "Phone");

        var initial = await UploadAsync(client, vaultId, firstDevice, "initial", "Chili.md", 0, "base");
        var desktop = await UploadAsync(client, vaultId, firstDevice, "desktop-edit", "Chili.md", initial.Revision, "desktop");
        var phone = await UploadAsync(client, vaultId, secondDevice, "phone-edit", "Chili.md", initial.Revision, "phone");

        Assert.Equal("accepted", desktop.Status);
        Assert.Equal("conflict", phone.Status);
        Assert.NotEqual("Chili.md", phone.Path);
        Assert.False(phone.Replay);

        var retry = await UploadAsync(client, vaultId, secondDevice, "phone-edit", "Chili.md", initial.Revision, "phone");
        Assert.Equal("conflict", retry.Status);
        Assert.True(retry.Replay);
        Assert.Equal(phone.Revision, retry.Revision);

        var original = await client.GetByteArrayAsync($"/api/vaults/{vaultId}/files/{desktop.FileId}/content");
        Assert.Equal("desktop", System.Text.Encoding.UTF8.GetString(original));
        var conflict = await client.GetByteArrayAsync($"/api/vaults/{vaultId}/files/{phone.FileId}/content");
        Assert.Equal("phone", System.Text.Encoding.UTF8.GetString(conflict));
    }

    [Fact]
    public async Task IndependentFilesAndBinaryObjectsConvergeWithoutDuplicateContent()
    {
        using var client = await CreateAuthenticatedClientAsync("independent-user");
        var vaultId = await CreateVaultAsync(client, "Files");
        var firstDevice = await CreateDeviceAsync(client, "Desktop");
        var secondDevice = await CreateDeviceAsync(client, "Tablet");

        var one = await UploadAsync(client, vaultId, firstDevice, "file-one", "File1.md", 0, "alpha");
        var two = await UploadAsync(client, vaultId, secondDevice, "file-two", "File2.md", 0, "beta");
        Assert.Equal(1, one.Revision);
        Assert.Equal(2, two.Revision);

        var bytes = new byte[] { 0, 1, 2, 3, 255 };
        var binaryOne = await UploadBytesAsync(client, vaultId, firstDevice, "binary-one", "assets/blob.bin", 0, bytes);
        var binaryTwo = await UploadBytesAsync(client, vaultId, secondDevice, "binary-two", "assets/blob-copy.bin", 0, bytes);
        Assert.Equal(binaryOne.ContentHash, binaryTwo.ContentHash);
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/vaults/{vaultId}/files/{binaryTwo.FileId}/content"));

        var objectFiles = Directory.GetFiles(Path.Combine(_factory.DataPath, "objects"), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".tmp" + Path.DirectorySeparatorChar))
            .ToArray();
        Assert.Equal(3, objectFiles.Length);
    }

    [Fact]
    public async Task HealthEndpointIsHealthyAndTraversalIsRejected()
    {
        using var client = _factory.CreateClient();
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var authenticated = await CreateAuthenticatedClientAsync("path-user");
        var vaultId = await CreateVaultAsync(authenticated, "Paths");
        var deviceId = await CreateDeviceAsync(authenticated, "Laptop");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(deviceId.ToString()), "deviceId");
        form.Add(new StringContent("bad-path"), "operationId");
        form.Add(new StringContent("../outside.md"), "path");
        form.Add(new StringContent("0"), "baseFileRevision");
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("no")), "content", "outside.md");
        var response = await authenticated.PostAsync($"/api/vaults/{vaultId}/sync/upload", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(_json);
        Assert.Equal("invalid_request", error!.Error);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string userName)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new { userName, password = "correct horse battery staple" })
        };
        request.Headers.Add("X-Registration-Key", "test-registration-key");
        var register = await client.SendAsync(request);
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResponse>(_json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        return client;
    }

    private async Task<Guid> CreateVaultAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/vaults", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VaultSummary>(_json))!.Id;
    }

    private async Task<Guid> CreateDeviceAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/devices", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceSummary>(_json))!.Id;
    }

    private async Task<SyncMutationResponse> UploadAsync(HttpClient client, Guid vaultId, Guid deviceId, string operationId, string path, long baseRevision, string text)
    {
        return await UploadBytesAsync(client, vaultId, deviceId, operationId, path, baseRevision, System.Text.Encoding.UTF8.GetBytes(text));
    }

    private async Task<SyncMutationResponse> UploadBytesAsync(HttpClient client, Guid vaultId, Guid deviceId, string operationId, string path, long baseRevision, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(deviceId.ToString()), "deviceId");
        form.Add(new StringContent(operationId), "operationId");
        form.Add(new StringContent(path), "path");
        form.Add(new StringContent(baseRevision.ToString()), "baseFileRevision");
        form.Add(new ByteArrayContent(bytes), "content", Path.GetFileName(path));
        var response = await client.PostAsync($"/api/vaults/{vaultId}/sync/upload", form);
        var result = await response.Content.ReadFromJsonAsync<SyncMutationResponse>(_json);
        Assert.NotNull(result);
        return result!;
    }

    private sealed record AuthResponse(string Token, DateTime ExpiresUtc, UserSummary User);
    private sealed record UserSummary(Guid Id, string UserName);
    private sealed record VaultSummary(Guid Id, string Name, long CurrentRevision, string Role);
    private sealed record DeviceSummary(Guid Id, string Name, DateTime LastSeenUtc);
    private sealed record ChangeDto(Guid FileId, long Revision, string Operation, string Path, string? OldPath, string? ContentHash, long Size, bool IsConflict, long BaseFileRevision, DateTime CreatedUtc);
    private sealed record ChangesResponse(Guid VaultId, long CurrentRevision, List<ChangeDto> Changes);
    private sealed record SyncMutationResponse(string Status, Guid FileId, long Revision, string Path, string? OldPath, string? ContentHash, long Size, long CurrentFileRevision, long CurrentVaultRevision, bool Replay, string? ConflictPath);
    private sealed record HistoryDto(Guid Id, long Revision, string Operation, string Path, string? OldPath, string? ContentHash, long Size, bool IsConflict, DateTime CreatedUtc, Guid? CreatedByDeviceId);
    private sealed record ErrorResponse(string Error, string? Detail);
}

public sealed class SyncApplicationFactory : WebApplicationFactory<Program>
{
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "obsync-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DATA_PATH", DataPath);
        builder.UseSetting("JWT_SIGNING_KEY", "test-signing-key-that-is-longer-than-thirty-two-characters");
        builder.UseSetting("REGISTRATION_KEY", "test-registration-key");
        builder.UseSetting("ADMIN_USERNAME", "admin");
        builder.UseSetting("ADMIN_PASSWORD", "admin-test-password");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(DataPath))
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Directory.Delete(DataPath, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 9)
                {
                    Thread.Sleep(100);
                }
                catch (IOException)
                {
                    // SQLite can keep a file handle briefly after the test host stops.
                    // The unique temp directory is safe to leave for the OS cleanup pass.
                }
            }
        }
    }
}
