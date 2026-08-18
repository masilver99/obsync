using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using System.Text.Json.Serialization;
using ObsidianSync.Server.Api;
using ObsidianSync.Server.Data;
using ObsidianSync.Server.Security;
using ObsidianSync.Server.Services;
using ObsidianSync.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var dataPath = builder.Configuration["DATA_PATH"]
    ?? builder.Configuration["DataPath"]
    ?? Path.Combine(AppContext.BaseDirectory, "data");
dataPath = Path.GetFullPath(dataPath);
Directory.CreateDirectory(dataPath);

var connectionString = builder.Configuration.GetConnectionString("SyncDb")
    ?? $"Data Source={Path.Combine(dataPath, "sync.db")};Cache=Shared;Default Timeout=30";
var corsOrigins = (builder.Configuration["CORS_ORIGINS"] ?? "app://obsidian.md")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddDbContext<SyncDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSingleton<IObjectStore>(_ => new FileSystemObjectStore(Path.Combine(dataPath, "objects")));
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<RegistrationGate>();
builder.Services.AddCors(options => options.AddPolicy("plugin", policy =>
    policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));
var jwtTokenService = new JwtTokenService(builder.Configuration);
builder.Services.AddSingleton(jwtTokenService);
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<AdminService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.TokenValidationParameters = jwtTokenService.CreateValidationParameters());
builder.Services.AddAuthorization(options =>
    options.AddPolicy("AdminOnly", policy => policy.RequireClaim("obsync_admin", "true")));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
    await DbInitializer.InitializeAsync(db);
    await AdminBootstrapper.EnsureAsync(
        db,
        scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(),
        builder.Configuration,
        app.Logger);
}

var staticRoot = new[]
{
    Path.Combine(builder.Environment.ContentRootPath, "wwwroot"),
    Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "wwwroot"))
}.FirstOrDefault(Directory.Exists);

if (staticRoot is not null)
{
    var staticFileProvider = new PhysicalFileProvider(staticRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = staticFileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = staticFileProvider });
}
else
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
app.UseCors("plugin");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "obsync", status = "ok" }));
app.MapGet("/health", async (SyncDbContext db, IObjectStore objectStore, CancellationToken cancellationToken) =>
{
    var databaseHealthy = false;
    var objectStoreHealthy = false;
    try
    {
        databaseHealthy = await db.Database.CanConnectAsync(cancellationToken);
        objectStoreHealthy = await objectStore.CheckWritableAsync(cancellationToken);
    }
    catch
    {
        // The response below deliberately avoids exposing connection details.
    }

    var healthy = databaseHealthy && objectStoreHealthy;
    var result = new
    {
        status = healthy ? "healthy" : "degraded",
        sqlite = databaseHealthy,
        objects = objectStoreHealthy
    };
    return healthy ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
});

EndpointMapping.Map(app);

app.Run();

public partial class Program;
