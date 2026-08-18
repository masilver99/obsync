using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ObsidianSync.Server.Tests;

public sealed class AdminApiTests : IClassFixture<SyncApplicationFactory>
{
    private readonly SyncApplicationFactory _factory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public AdminApiTests(SyncApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminShellIsServedAtTheDashboardRoute()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Obsync administration", html);
    }

    [Fact]
    public async Task AdminCanManageUsersVaultAccessAndSeeCompletedSyncs()
    {
        using var admin = await LoginAsync("admin", "admin-test-password");

        var createdUser = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            userName = "managed-user",
            password = "managed-test-password",
            isAdmin = false
        });
        Assert.Equal(HttpStatusCode.OK, createdUser.StatusCode);

        var vaultResponse = await admin.PostAsJsonAsync("/api/vaults", new { name = "Managed vault" });
        vaultResponse.EnsureSuccessStatusCode();
        var vault = await vaultResponse.Content.ReadFromJsonAsync<VaultSummary>(_json);
        Assert.NotNull(vault);

        var deviceResponse = await admin.PostAsJsonAsync("/api/devices", new { name = "Admin device" });
        deviceResponse.EnsureSuccessStatusCode();
        var device = await deviceResponse.Content.ReadFromJsonAsync<DeviceSummary>(_json);
        Assert.NotNull(device);

        var heartbeat = await admin.PostAsJsonAsync($"/api/vaults/{vault!.Id}/sync/heartbeat", new
        {
            deviceId = device!.Id,
            status = "completed",
            lastKnownRevision = 0
        });
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);

        var membership = await admin.PutAsJsonAsync($"/api/admin/vaults/{vault.Id}/members", new
        {
            userName = "managed-user",
            role = "Editor"
        });
        Assert.Equal(HttpStatusCode.NoContent, membership.StatusCode);

        var dashboardResponse = await admin.GetAsync("/api/admin/dashboard");
        dashboardResponse.EnsureSuccessStatusCode();
        var dashboard = await dashboardResponse.Content.ReadFromJsonAsync<Dashboard>(_json);
        Assert.NotNull(dashboard);
        var dashboardUser = Assert.Single(dashboard!.Users, user => user.UserName == "managed-user");
        Assert.Contains("Managed vault", dashboardUser.VaultNames);
        var dashboardVault = Assert.Single(dashboard.Vaults, item => item.Id == vault.Id);
        Assert.Equal(2, dashboardVault.MemberCount);
        Assert.NotNull(dashboardVault.LastSuccessfulSyncUtc);

        using var managed = await LoginAsync("managed-user", "managed-test-password");
        var vaults = await managed.GetFromJsonAsync<List<VaultSummary>>("/api/vaults", _json);
        Assert.NotNull(vaults);
        Assert.Contains(vaults!, item => item.Id == vault.Id && item.Role == "Editor");
    }

    [Fact]
    public async Task RegularUsersCannotReadAdminDashboard()
    {
        using var client = _factory.CreateClient();
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new { userName = "regular-admin-test", password = "regular-test-password" })
        };
        register.Headers.Add("X-Registration-Key", "test-registration-key");
        var registration = await client.SendAsync(register);
        registration.EnsureSuccessStatusCode();
        var auth = await registration.Content.ReadFromJsonAsync<AuthResponse>(_json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);

        var response = await client.GetAsync("/api/admin/dashboard");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<HttpClient> LoginAsync(string userName, string password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(_json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        return client;
    }

    private sealed record AuthResponse(string Token, DateTime ExpiresUtc, UserSummary User);
    private sealed record UserSummary(Guid Id, string UserName);
    private sealed record VaultSummary(Guid Id, string Name, long CurrentRevision, string Role);
    private sealed record DeviceSummary(Guid Id, string Name, DateTime LastSeenUtc);
    private sealed record Dashboard(DashboardOverview Overview, List<DashboardUser> Users, List<DashboardVault> Vaults);
    private sealed record DashboardOverview(int UserCount, int VaultCount, int DeviceCount, int ActiveFileCount, long LogicalBytes, long ObjectCount, long ObjectBytes, DateTime? LastSuccessfulSyncUtc);
    private sealed record DashboardUser(Guid Id, string UserName, bool IsAdmin, DateTime CreatedUtc, int DeviceCount, int VaultCount, List<string> VaultNames, DateTime? LastSeenUtc);
    private sealed record DashboardVault(Guid Id, string Name, string OwnerUserName, long CurrentRevision, int MemberCount, int FileCount, long LogicalBytes, int RevisionCount, DateTime? LastSuccessfulSyncUtc);
}
