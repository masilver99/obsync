using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ObsidianSync.Server.Contracts;
using ObsidianSync.Server.Data;
using ObsidianSync.Server.Security;
using ObsidianSync.Server.Services;

namespace ObsidianSync.Server.Api;

public static class EndpointMapping
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/auth/register", async (
            RegisterRequest request,
            HttpRequest httpRequest,
            SyncDbContext db,
            IPasswordHasher<User> passwordHasher,
            JwtTokenService tokenService,
            RegistrationGate registrationGate,
            CancellationToken cancellationToken) =>
        {
            var suppliedRegistrationKey = httpRequest.Headers["X-Registration-Key"].ToString();
            if (!registrationGate.IsEnabled)
            {
                return Results.Json(new ErrorResponse("registration_disabled", "Registration is disabled on this server."), statusCode: StatusCodes.Status403Forbidden);
            }

            if (!registrationGate.IsValid(suppliedRegistrationKey))
            {
                return Results.Json(new ErrorResponse("registration_forbidden", "A valid registration key is required."), statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length > 200 || request.Password.Length < 8)
            {
                return Results.BadRequest(new ErrorResponse("invalid_request", "UserName is required and passwords must contain at least 8 characters."));
            }

            var userName = request.UserName.Trim();
            var normalized = SyncService.NormalizeUserName(userName);
            if (await db.Users.AnyAsync(user => user.NormalizedUserName == normalized, cancellationToken))
            {
                return Results.Conflict(new ErrorResponse("user_exists", "That username is already registered."));
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = userName,
                NormalizedUserName = normalized,
                PasswordHash = string.Empty,
                CreatedUtc = DateTime.UtcNow
            };
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(CreateAuthResponse(user, tokenService));
        });

        app.MapPost("/api/auth/login", async (
            LoginRequest request,
            SyncDbContext db,
            IPasswordHasher<User> passwordHasher,
            JwtTokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            var normalized = SyncService.NormalizeUserName(request.UserName ?? string.Empty);
            var user = await db.Users.SingleOrDefaultAsync(item => item.NormalizedUserName == normalized, cancellationToken);
            if (user is null || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(CreateAuthResponse(user, tokenService));
        });

        var authorized = app.MapGroup("/api").RequireAuthorization();

        var admin = app.MapGroup("/api/admin").RequireAuthorization("AdminOnly");
        admin.MapGet("/dashboard", async (AdminService service, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.GetDashboardAsync(cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        admin.MapPost("/users", async (AdminCreateUserRequest request, AdminService service, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.CreateUserAsync(request, cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        admin.MapPost("/users/{userId:guid}/password", async (Guid userId, AdminSetPasswordRequest request, AdminService service, CancellationToken cancellationToken) =>
        {
            try
            {
                await service.SetPasswordAsync(userId, request, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        admin.MapPut("/vaults/{vaultId:guid}/members", async (Guid vaultId, AdminMemberRequest request, AdminService service, CancellationToken cancellationToken) =>
        {
            try
            {
                await service.SetMembershipAsync(vaultId, request, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        admin.MapDelete("/vaults/{vaultId:guid}/members/{userId:guid}", async (Guid vaultId, Guid userId, AdminService service, CancellationToken cancellationToken) =>
        {
            try
            {
                await service.RemoveMembershipAsync(vaultId, userId, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapGet("/me", async (HttpContext context, SyncDbContext db, CancellationToken cancellationToken) =>
        {
            var userId = context.User.RequireUserId();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
            return user is null
                ? Results.NotFound(new ErrorResponse("user_not_found"))
                : Results.Ok(new UserSummary(user.Id, user.UserName));
        });

        authorized.MapGet("/vaults", async (HttpContext context, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sync.GetVaultsAsync(context.User.RequireUserId(), cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapPost("/vaults", async (HttpContext context, CreateVaultRequest request, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sync.CreateVaultAsync(context.User.RequireUserId(), request.Name, cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapGet("/devices", async (HttpContext context, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sync.GetDevicesAsync(context.User.RequireUserId(), cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapPost("/devices", async (HttpContext context, CreateDeviceRequest request, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sync.CreateDeviceAsync(context.User.RequireUserId(), request.Name, cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapGet("/vaults/{vaultId:guid}/members", async (HttpContext context, Guid vaultId, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sync.GetMembersAsync(context.User.RequireUserId(), vaultId, cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapPost("/vaults/{vaultId:guid}/members", async (HttpContext context, Guid vaultId, AddMemberRequest request, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                await sync.AddMemberAsync(context.User.RequireUserId(), vaultId, request, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapGet("/vaults/{vaultId:guid}/changes", async (HttpContext context, Guid vaultId, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                var after = ParseLong(context.Request.Query["after"].ToString(), 0);
                var limit = ParseInt(context.Request.Query["limit"].ToString(), 500);
                return Results.Ok(await sync.GetChangesAsync(context.User.RequireUserId(), vaultId, after, limit, cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapPost("/vaults/{vaultId:guid}/sync/heartbeat", async (HttpContext context, Guid vaultId, SyncHeartbeatRequest request, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sync.RecordHeartbeatAsync(context.User.RequireUserId(), vaultId, request, cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapPost("/vaults/{vaultId:guid}/sync/upload", async (HttpContext context, Guid vaultId, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                var form = await context.Request.ReadFormAsync(cancellationToken);
                var file = form.Files.GetFile("content");
                if (file is null)
                {
                    return Results.BadRequest(new ErrorResponse("invalid_request", "The multipart field 'content' is required."));
                }

                if (!Guid.TryParse(form["deviceId"].ToString(), out var deviceId) ||
                    !long.TryParse(form["baseFileRevision"].ToString(), out var baseFileRevision))
                {
                    return Results.BadRequest(new ErrorResponse("invalid_request", "deviceId and baseFileRevision are required."));
                }

                await using var content = file.OpenReadStream();
                return Results.Ok(await sync.UploadAsync(
                    context.User.RequireUserId(),
                    vaultId,
                    deviceId,
                    form["operationId"].ToString(),
                    form["path"].ToString(),
                    baseFileRevision,
                    content,
                    cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapPost("/vaults/{vaultId:guid}/sync/delete", async (HttpContext context, Guid vaultId, DeleteRequest request, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sync.DeleteAsync(context.User.RequireUserId(), vaultId, request, cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapPost("/vaults/{vaultId:guid}/sync/rename", async (HttpContext context, Guid vaultId, RenameRequest request, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sync.RenameAsync(context.User.RequireUserId(), vaultId, request, cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapGet("/vaults/{vaultId:guid}/files/{fileId:guid}/history", async (HttpContext context, Guid vaultId, Guid fileId, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await sync.GetHistoryAsync(context.User.RequireUserId(), vaultId, fileId, cancellationToken));
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });

        authorized.MapGet("/vaults/{vaultId:guid}/files/{fileId:guid}/content", async (HttpContext context, Guid vaultId, Guid fileId, SyncService sync, CancellationToken cancellationToken) =>
        {
            try
            {
                var revision = context.Request.Query["revision"].Count == 0
                    ? (long?)null
                    : ParseLong(context.Request.Query["revision"].ToString(), 0);
                var result = await sync.OpenContentAsync(context.User.RequireUserId(), vaultId, fileId, revision, cancellationToken);
                return Results.Stream(result.Content, "application/octet-stream");
            }
            catch (Exception exception)
            {
                return ToError(exception);
            }
        });
    }

    private static AuthResponse CreateAuthResponse(User user, JwtTokenService tokenService) => new(
        tokenService.CreateToken(user),
        DateTime.UtcNow.AddDays(30),
        new UserSummary(user.Id, user.UserName));

    private static long ParseLong(string value, long fallback) => long.TryParse(value, out var result) ? result : fallback;
    private static int ParseInt(string value, int fallback) => int.TryParse(value, out var result) ? result : fallback;

    private static IResult ToError(Exception exception) => exception switch
    {
        ArgumentException => Results.BadRequest(new ErrorResponse("invalid_request", exception.Message)),
        KeyNotFoundException => Results.NotFound(new ErrorResponse("not_found", exception.Message)),
        SyncConflictException => Results.Conflict(new ErrorResponse("conflict", exception.Message)),
        UnauthorizedAccessException => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.Problem("The server could not complete the request.")
    };
}
