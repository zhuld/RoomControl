// RoomControl API 入口：配置数据库、认证、限流与后台服务，并注册全部 REST 端点与 SignalR Hub。
// 端点按 品牌 → 位置 → 教室 → 中控 → 控制点 → 定时任务 → 日志 排列；字段约束与响应示例见 docs/api.md。
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Quartz;
using RoomControl.Api.Contracts;
using RoomControl.Api.Data;
using RoomControl.Api.Hubs;
using RoomControl.Api.Integrations;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("RoomControl")
    ?? throw new InvalidOperationException("Set ConnectionStrings__RoomControl before starting the API.");
var databaseProvider = builder.Configuration["Database:Provider"] ?? "PostgreSQL";

// 数据库 Provider 和连接串由环境配置控制，便于本地 SQLite 与部署数据库复用同一模型。
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContextFactory<ControlDbContext>(options =>
{
    if (string.Equals(databaseProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(connectionString);
    }
    else if (string.Equals(databaseProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(connectionString);
    }
    else
    {
        throw new InvalidOperationException($"Unsupported database provider '{databaseProvider}'.");
    }
});
// Cookie 在每次请求时重新核验账号状态和角色，使管理员变更立即生效。
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "RoomControl.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var username = context.Principal?.Identity?.Name;
            var activeRole = string.IsNullOrWhiteSpace(username)
                ? null
                : context.HttpContext.RequestServices.GetRequiredService<UserService>().GetActiveRole(username);
            if (activeRole is null)
            {
                // 账号被删除或禁用后立即失效，不再等待 Cookie 自然过期。
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            var roleName = activeRole.Value.ToName();
            if (context.Principal?.FindFirst(ClaimTypes.Role)?.Value == roleName)
            {
                return;
            }

            // 角色被管理员调整后同步到当前 Cookie，避免旧权限继续生效。
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, username!),
                    new Claim(ClaimTypes.Role, roleName),
                ],
                CookieAuthenticationDefaults.AuthenticationScheme,
                ClaimTypes.Name,
                ClaimTypes.Role);
            context.ReplacePrincipal(new ClaimsPrincipal(identity));
            context.ShouldRenew = true;
        };
    });
builder.Services.AddSignalR();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // 仅信任默认的本机代理（KnownNetworks/KnownProxies 默认包含 loopback），
    // 反向代理终止 TLS 时据此识别原始协议与客户端 IP；远程代理需显式配置 KnownProxies。
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
// 登录与登录账号列表分别限流，避免公开入口被高频枚举或尝试。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("accounts", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});
// 注册后台基础设施及业务服务；控制器连接服务同时作为托管后台任务运行。
builder.Services.AddQuartz(quartz =>
{
    var auditLogCleanupJobKey = new JobKey("audit-log-cleanup");
    quartz.AddJob<AuditLogCleanupJob>(options => options.WithIdentity(auditLogCleanupJobKey));
    // 每日凌晨 3 点按站点保留天数清理过期日志，避免审计表无限增长。
    quartz.AddTrigger(options => options
        .ForJob(auditLogCleanupJobKey)
        .WithIdentity("audit-log-cleanup-trigger")
        .WithCronSchedule("0 0 3 * * ?"));
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<UserService>();
builder.Services.AddSingleton<AuditLogWriter>();
builder.Services.AddSingleton<AuditLogRetentionService>();
builder.Services.AddScoped<AuditLogTargetResolver>();
builder.Services.AddSingleton<IControllerAdapterFactory, ControllerAdapterFactory>();
builder.Services.AddSingleton<ControllerStateStore>();
builder.Services.AddSingleton<ControllerConnectionService>();
builder.Services.AddHostedService(services => services.GetRequiredService<ControllerConnectionService>());
builder.Services.AddSingleton<ScheduleService>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireClaim(ClaimTypes.Role, UserRoleExtensions.AdminName));
    options.AddPolicy("OperatorOrAdmin", policy => policy.RequireAssertion(ctx =>
        ctx.User.IsInRole(UserRoleExtensions.AdminName) || ctx.User.IsInRole(UserRoleExtensions.OperatorName)));
});

var app = builder.Build();

// 先解析可信代理头，后续限流和 Cookie 安全策略才能读取原始客户端信息。
app.UseForwardedHeaders();

app.UseRateLimiter();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (statusCode, title) = exception switch
    {
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
        ControllerUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Controller unavailable"),
        InvalidOperationException => (StatusCodes.Status409Conflict, "Operation cannot be completed"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected server error")
    };

    if (statusCode >= StatusCodes.Status500InternalServerError && exception is not null)
    {
        app.Logger.LogError(exception, "Unhandled request failure.");
    }

    context.Response.StatusCode = statusCode;
    await Results.Problem(
        statusCode: statusCode,
        title: title,
        detail: statusCode >= 500 ? null : exception?.Message).ExecuteAsync(context);
}));

// 部署包将 Vue 构建产物放在 wwwroot；静态页面仍使用同源 API 和 SignalR 地址。
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AuditLogMiddleware>();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
    // 创建新库并向前兼容升级旧表，然后恢复调度任务和初始管理员。
    await db.Database.EnsureCreatedAsync();
    await SchemaUpgrader.UpgradeAsync(db, CancellationToken.None);
    await SeedLocationsAsync(db, CancellationToken.None);
    // 启动时先按保留策略清理一次，避免停机期间积累的过期日志继续留在库中。
    await scope.ServiceProvider.GetRequiredService<AuditLogRetentionService>()
        .PurgeExpiredAsync(CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<ScheduleService>().RestoreAsync(CancellationToken.None);

    var adminUser = builder.Configuration["Authentication:AdminUser"];
    var adminPassword = builder.Configuration["Authentication:AdminPassword"];
    var userService = scope.ServiceProvider.GetRequiredService<UserService>();
    userService.EnsureDefaultAdmin(adminUser ?? "admin", adminPassword ?? string.Empty);
}

// 登录页可匿名读取品牌和健康状态；账号列表也公开，但单独受限流保护。
app.MapGet("/api/branding", async (ControlDbContext db, CancellationToken cancellationToken) =>
{
    var settings = await db.SiteSettings.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == SiteSettings.SingletonId, cancellationToken);
    return Results.Ok(new
    {
        DisplayName = settings?.DisplayName ?? SiteSettings.DefaultDisplayName,
        Subtitle = settings?.Subtitle ?? SiteSettings.DefaultSubtitle,
        LogoDataUrl = settings?.LogoDataUrl
    });
}).AllowAnonymous();

// 健康检查用于安装脚本与反向代理探活，同时验证数据库连接。
app.MapGet("/health", async (ControlDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ok", database = "ok" })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable"));

app.MapGet("/api/auth/accounts", (UserService userService) =>
    Results.Ok(userService.GetLoginAccounts())).AllowAnonymous().RequireRateLimiting("accounts");

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    UserService userService,
    LoginThrottle loginThrottle,
    HttpContext context) =>
{
    // 锁定期间返回与凭据错误一致的 401，避免暴露账号是否存在或已触发锁定。
    if (loginThrottle.IsBlocked(request.Username))
    {
        return Results.Unauthorized();
    }

    var authResult = userService.Authenticate(request.Username, request.Password);
    if (authResult is null)
    {
        loginThrottle.RecordFailure(request.Username);
        return Results.Unauthorized();
    }

    loginThrottle.Reset(request.Username);
    var (role, username) = authResult.Value;
    // 登录成功后当前请求尚未带上身份，把账号交给审计日志记录到对象栏。
    context.Items[AuditLogAuthTarget.UsernameItemKey] = username;
    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, username),
        new(ClaimTypes.Role, role.ToName()),
    };
    var identity = new ClaimsIdentity(
        claims,
        CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));
    return Results.NoContent();
}).AllowAnonymous().RequireRateLimiting("login");

// 除显式 AllowAnonymous 的端点外，API 默认要求有效会话。
var api = app.MapGroup("/api").RequireAuthorization();

api.MapPost("/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});

api.MapGet("/auth/me", (ClaimsPrincipal user) => Results.Ok(new
{
    username = user.Identity?.Name,
    role = user.FindFirst(ClaimTypes.Role)?.Value ?? "viewer",
}));

api.MapPost("/auth/change-password", async (
    ChangePasswordRequest request,
    UserService userService,
    ClaimsPrincipal user) =>
{
    var username = user.Identity?.Name ?? string.Empty;
    if (userService.ChangePassword(username, request.OldPassword, request.NewPassword))
    {
        return Results.NoContent();
    }
    return Results.BadRequest(new { detail = "原密码不正确或账号已被禁用。" });
});

api.MapGet("/logs", async (
    string? category,
    DateTimeOffset? from,
    DateTimeOffset? to,
    int? limit,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!string.IsNullOrWhiteSpace(category) && !AuditLogCategories.Contains(category))
    {
        throw new ArgumentException("Unsupported log category.");
    }
    if (from is not null && to is not null && from > to)
    {
        throw new ArgumentException("开始时间不能晚于结束时间。");
    }

    var query = db.AuditLogs.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(category))
    {
        query = query.Where(log => log.Category == category);
    }
    if (from is { } fromValue)
    {
        query = query.Where(log => log.OccurredAtUtc >= fromValue);
    }
    if (to is { } toValue)
    {
        query = query.Where(log => log.OccurredAtUtc <= toValue);
    }

    IQueryable<AuditLog> page = query.OrderByDescending(log => log.OccurredAtUtc);
    // 未指定 limit 时返回全部保留记录；limit 仅供调用方主动限制条数，不再有 500 条上限。
    if (limit is { } pageSize)
    {
        page = page.Take(Math.Max(pageSize, 1));
    }

    var logs = await page
        .Select(log => new
        {
            log.Id,
            log.Category,
            log.Action,
            log.Target,
            log.Actor,
            log.StatusCode,
            log.OccurredAtUtc
        })
        .ToListAsync(cancellationToken);
    return Results.Ok(logs);
}).RequireAuthorization();

api.MapGet("/logs/settings", async (
    AuditLogRetentionService retentionService,
    CancellationToken cancellationToken) =>
{
    var retentionDays = await retentionService.GetRetentionDaysAsync(cancellationToken);
    return Results.Ok(new { RetentionDays = retentionDays });
}).RequireAuthorization("AdminOnly");

api.MapPut("/logs/settings", async (
    UpdateLogSettingsRequest request,
    AuditLogRetentionService retentionService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await retentionService.SetRetentionDaysAsync(request.RetentionDays, cancellationToken);
        // 保存后立即按新策略清理，避免过期记录保留到下一次定时清理。
        await retentionService.PurgeExpiredAsync(cancellationToken);
        return Results.Ok(new { RetentionDays = request.RetentionDays });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { detail = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

// 用户维护仅管理员可用；账号角色变更会由 Cookie 校验逻辑即时应用。
api.MapGet("/users", (UserService userService) =>
{
    var users = userService.GetUsers();
    return Results.Ok(users);
}).RequireAuthorization("AdminOnly");

api.MapPost("/users", async (
    CreateUserRequest request,
    UserService userService) =>
{
    try
    {
        var created = userService.CreateUser(request);
        return Results.Created($"/api/users/{created.Id}", created);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { detail = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

api.MapPut("/users/{id:guid}", async (
    Guid id,
    UpdateUserRequest request,
    UserService userService,
    ClaimsPrincipal currentUser) =>
{
    var username = currentUser.Identity?.Name ?? string.Empty;
    var updated = userService.UpdateUser(id, request, username);
    if (updated is null)
    {
        return Results.NotFound();
    }
    return Results.Ok(updated);
}).RequireAuthorization("AdminOnly");

api.MapDelete("/users/{id:guid}", async (
    Guid id,
    UserService userService,
    ClaimsPrincipal currentUser) =>
{
    var username = currentUser.Identity?.Name ?? string.Empty;
    try
    {
        var deleted = userService.DeleteUser(id, username);
        if (!deleted)
        {
            return Results.NotFound();
        }
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { detail = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

// 站点级配置和位置层级先于教室配置，供教室表单引用。
api.MapPut("/branding", async (
    UpdateBrandingRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 60)
    {
        throw new ArgumentException("Display name is required and must be at most 60 characters.");
    }

    var subtitle = request.Subtitle?.Trim() ?? string.Empty;
    if (subtitle.Length > 80)
    {
        throw new ArgumentException("Subtitle must be at most 80 characters.");
    }

    var logo = string.IsNullOrWhiteSpace(request.LogoDataUrl) ? null : request.LogoDataUrl;
    if (logo is not null &&
        (logo.Length > 300_000 || !logo.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
    {
        throw new ArgumentException("Logo must be an uploaded image no larger than 200 KB.");
    }

    var settings = await db.SiteSettings
        .SingleOrDefaultAsync(item => item.Id == SiteSettings.SingletonId, cancellationToken);
    if (settings is null)
    {
        settings = new SiteSettings { Id = SiteSettings.SingletonId };
        db.SiteSettings.Add(settings);
    }

    settings.DisplayName = request.DisplayName.Trim();
    settings.Subtitle = subtitle;
    settings.LogoDataUrl = logo;
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { settings.DisplayName, settings.Subtitle, settings.LogoDataUrl });
}).RequireAuthorization("AdminOnly");

// 位置、教室、中控和 Join 配置接口；删除上游对象时同步归档相关任务。
api.MapGet("/controller-types", () => Results.Ok(ControllerAdapterTypes.All));

api.MapGet("/buildings", async (ControlDbContext db, CancellationToken cancellationToken) =>
{
    var buildings = await db.Buildings.AsNoTracking()
        .Include(building => building.Floors)
        .OrderBy(building => building.Name)
        .ToListAsync(cancellationToken);
    return Results.Ok(buildings.Select(building => new
    {
        building.Id,
        building.Name,
        Floors = building.Floors
            .OrderBy(floor => floor.Name)
            .Select(floor => new { floor.Id, floor.Name })
    }));
});

api.MapPost("/buildings", async (
    CreateBuildingRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var name = NormalizeLocationName(request.Name, "楼栋");
    if (await db.Buildings.AnyAsync(building => building.Name == name, cancellationToken))
    {
        throw new InvalidOperationException($"楼栋「{name}」已存在。");
    }

    var building = new Building { Name = name };
    db.Buildings.Add(building);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/buildings/{building.Id}", new { building.Id, building.Name });
}).RequireAuthorization("AdminOnly");

api.MapPut("/buildings/{buildingId:guid}", async (
    Guid buildingId,
    CreateBuildingRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var name = NormalizeLocationName(request.Name, "楼栋");
    var building = await db.Buildings.SingleOrDefaultAsync(item => item.Id == buildingId, cancellationToken)
        ?? throw new KeyNotFoundException("Building was not found.");
    if (building.Name == name)
    {
        return Results.NoContent();
    }
    if (await db.Buildings.AnyAsync(item => item.Id != buildingId && item.Name == name, cancellationToken))
    {
        throw new InvalidOperationException($"楼栋「{name}」已存在。");
    }

    var rooms = await db.Rooms.Where(room => room.Building == building.Name).ToListAsync(cancellationToken);
    foreach (var room in rooms)
    {
        room.Building = name;
    }

    building.Name = name;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

api.MapDelete("/buildings/{buildingId:guid}", async (
    Guid buildingId,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var building = await db.Buildings.Include(item => item.Floors)
        .SingleOrDefaultAsync(item => item.Id == buildingId, cancellationToken)
        ?? throw new KeyNotFoundException("Building was not found.");
    var roomCount = await db.Rooms.CountAsync(room => room.Building == building.Name, cancellationToken);
    if (roomCount > 0)
    {
        throw new InvalidOperationException($"楼栋「{building.Name}」下仍有 {roomCount} 个教室，请先调整这些教室的所在楼栋。");
    }

    db.Buildings.Remove(building);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

api.MapPost("/buildings/{buildingId:guid}/floors", async (
    Guid buildingId,
    CreateFloorRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var name = NormalizeLocationName(request.Name, "楼层");
    var building = await db.Buildings.SingleOrDefaultAsync(item => item.Id == buildingId, cancellationToken)
        ?? throw new KeyNotFoundException("Building was not found.");
    if (await db.Floors.AnyAsync(
            floor => floor.BuildingId == buildingId && floor.Name == name,
            cancellationToken))
    {
        throw new InvalidOperationException($"楼栋「{building.Name}」下已存在楼层「{name}」。");
    }

    var floor = new Floor { BuildingId = buildingId, Name = name };
    db.Floors.Add(floor);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/floors/{floor.Id}", new { floor.Id, floor.BuildingId, floor.Name });
}).RequireAuthorization("AdminOnly");

api.MapPut("/floors/{floorId:guid}", async (
    Guid floorId,
    CreateFloorRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var name = NormalizeLocationName(request.Name, "楼层");
    var floor = await db.Floors.Include(item => item.Building)
        .SingleOrDefaultAsync(item => item.Id == floorId, cancellationToken)
        ?? throw new KeyNotFoundException("Floor was not found.");
    if (floor.Name == name)
    {
        return Results.NoContent();
    }
    if (await db.Floors.AnyAsync(
            item => item.Id != floorId && item.BuildingId == floor.BuildingId && item.Name == name,
            cancellationToken))
    {
        throw new InvalidOperationException($"楼栋「{floor.Building.Name}」下已存在楼层「{name}」。");
    }

    var rooms = await db.Rooms
        .Where(room => room.Building == floor.Building.Name && room.Floor == floor.Name)
        .ToListAsync(cancellationToken);
    foreach (var room in rooms)
    {
        room.Floor = name;
    }

    floor.Name = name;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

api.MapDelete("/floors/{floorId:guid}", async (
    Guid floorId,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var floor = await db.Floors.Include(item => item.Building)
        .SingleOrDefaultAsync(item => item.Id == floorId, cancellationToken)
        ?? throw new KeyNotFoundException("Floor was not found.");
    var roomCount = await db.Rooms.CountAsync(
        room => room.Building == floor.Building.Name && room.Floor == floor.Name,
        cancellationToken);
    if (roomCount > 0)
    {
        throw new InvalidOperationException($"楼层「{floor.Name}」下仍有 {roomCount} 个教室，请先调整这些教室的所在楼层。");
    }

    db.Floors.Remove(floor);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

// 控制台读取房间树；写操作管理房间及其一对一的中控连接。
api.MapGet("/rooms", async (ControlDbContext db, CancellationToken cancellationToken) =>
{
    var rooms = await db.Rooms.AsNoTracking()
        .Include(room => room.Controller)
        .ThenInclude(controller => controller!.JoinMappings)
        .OrderBy(room => room.Name)
        .ToListAsync(cancellationToken);
    var controllerIds = rooms.Where(room => room.Controller is not null)
        .Select(room => room.Controller!.Id)
        .ToArray();
    var snapshots = await db.JoinSnapshots.AsNoTracking()
        .Where(snapshot =>
            controllerIds.Contains(snapshot.ControllerId) &&
            snapshot.Kind == JoinKind.Digital)
        .ToDictionaryAsync(
            snapshot => $"{snapshot.ControllerId}:{snapshot.JoinNumber}",
            cancellationToken);

    JoinStateSnapshotResponse ReadSnapshot(Guid controllerId, int joinNumber)
    {
        var snapshot = snapshots.GetValueOrDefault($"{controllerId}:{joinNumber}");
        return new JoinStateSnapshotResponse(snapshot?.DigitalValue ?? false, snapshot?.LastReportedAtUtc);
    }

    return Results.Ok(rooms.Select(room => new
    {
        room.Id,
        room.Name,
        room.Building,
        room.Floor,
        Controller = room.Controller is null ? null : new
        {
            room.Controller.Id,
            room.Controller.Name,
            room.Controller.AdapterType,
            room.Controller.Host,
            room.Controller.Port,
            room.Controller.Ipid,
            room.Controller.Enabled,
            room.Controller.IsOnline,
            room.Controller.LastError,
            Joins = room.Controller.JoinMappings
                .Where(join => join.Kind == JoinKind.Digital)
                .OrderBy(join => join.SortOrder)
                .ThenBy(join => join.Name)
                .Select(join => new
                {
                    join.Id,
                    join.Name,
                    join.SendJoinNumber,
                    join.StateJoinNumber,
                    join.EnableJoinNumber,
                    join.ShowConfirmationPopup,
                    StateSnapshot = ReadSnapshot(room.Controller.Id, join.StateJoinNumber),
                    EnableSnapshot = join.EnableJoinNumber > 0
                        ? ReadSnapshot(room.Controller.Id, join.EnableJoinNumber)
                        : null
                })
        }
    }));
});

api.MapPost("/rooms", async (
    CreateRoomRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var (name, building, floor) = NormalizeRoom(request);
    await EnsureLocationAsync(db, building, floor, cancellationToken);
    var room = new Room
    {
        Name = name,
        Building = building,
        Floor = floor
    };
    db.Rooms.Add(room);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/rooms/{room.Id}", room);
}).RequireAuthorization("AdminOnly");

api.MapPut("/rooms/{roomId:guid}", async (
    Guid roomId,
    CreateRoomRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var (name, building, floor) = NormalizeRoom(request);
    await EnsureLocationAsync(db, building, floor, cancellationToken);
    var room = await db.Rooms.SingleOrDefaultAsync(item => item.Id == roomId, cancellationToken)
        ?? throw new KeyNotFoundException("Room was not found.");
    room.Name = name;
    room.Building = building;
    room.Floor = floor;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

api.MapDelete("/rooms/{roomId:guid}", async (
    Guid roomId,
    ControlDbContext db,
    ScheduleService scheduleService,
    CancellationToken cancellationToken) =>
{
    var room = await db.Rooms.Include(item => item.Controller)
        .SingleOrDefaultAsync(item => item.Id == roomId, cancellationToken)
        ?? throw new KeyNotFoundException("Room was not found.");
    if (room.Controller is { } controller)
    {
        await scheduleService.DeleteForControllerAsync(controller.Id, cancellationToken);
        var snapshots = await db.JoinSnapshots.Where(item => item.ControllerId == controller.Id).ToListAsync(cancellationToken);
        db.JoinSnapshots.RemoveRange(snapshots);
    }

    db.Rooms.Remove(room);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

api.MapPost("/rooms/{roomId:guid}/controller", async (
    Guid roomId,
    CreateControllerRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var adapterType = ControllerAdapterTypes.Find(request.AdapterType)
        ?? throw new ArgumentException("请选择支持的中控类型。");
    if (string.IsNullOrWhiteSpace(request.Host))
    {
        throw new ArgumentException("Controller host is required.");
    }
    if (request.Port is < 1 or > 65535 || request.Ipid is < 0 or > 255)
    {
        throw new ArgumentException("Port must be 1-65535 and IPID must be 0-255.");
    }
    var room = await db.Rooms.Include(item => item.Controller)
        .SingleOrDefaultAsync(item => item.Id == roomId, cancellationToken)
        ?? throw new KeyNotFoundException("Room was not found.");
    if (room.Controller is not null)
    {
        throw new InvalidOperationException("A controller is already configured for this room.");
    }

    var controller = new Controller
    {
        RoomId = roomId,
        AdapterType = adapterType.Key,
        Name = adapterType.DisplayName,
        Host = request.Host.Trim(),
        Port = request.Port,
        Ipid = request.Ipid,
        Framing = CipFraming.Raw
    };
    db.Controllers.Add(controller);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/controllers/{controller.Id}", new
    {
        controller.Id,
        controller.RoomId,
        controller.Name,
        controller.AdapterType,
        controller.Host,
        controller.Port,
        controller.Ipid,
        controller.Enabled
    });
}).RequireAuthorization("AdminOnly");

api.MapPut("/controllers/{controllerId:guid}", async (
    Guid controllerId,
    UpdateControllerRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var adapterType = ControllerAdapterTypes.Find(request.AdapterType)
        ?? throw new ArgumentException("请选择支持的中控类型。");
    if (string.IsNullOrWhiteSpace(request.Host))
    {
        throw new ArgumentException("Controller host is required.");
    }
    if (request.Port is < 1 or > 65535 || request.Ipid is < 0 or > 255)
    {
        throw new ArgumentException("Invalid controller port or IPID.");
    }

    var controller = await db.Controllers.SingleOrDefaultAsync(item => item.Id == controllerId, cancellationToken)
        ?? throw new KeyNotFoundException("Controller was not found.");
    controller.AdapterType = adapterType.Key;
    controller.Name = adapterType.DisplayName;
    controller.Host = request.Host.Trim();
    controller.Port = request.Port;
    controller.Ipid = request.Ipid;
    controller.Enabled = request.Enabled;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

api.MapDelete("/controllers/{controllerId:guid}", async (
    Guid controllerId,
    ControlDbContext db,
    ScheduleService scheduleService,
    CancellationToken cancellationToken) =>
{
    var controller = await db.Controllers.SingleOrDefaultAsync(item => item.Id == controllerId, cancellationToken)
        ?? throw new KeyNotFoundException("Controller was not found.");

    await scheduleService.DeleteForControllerAsync(controllerId, cancellationToken);
    var snapshots = await db.JoinSnapshots.Where(item => item.ControllerId == controllerId).ToListAsync(cancellationToken);
    db.JoinSnapshots.RemoveRange(snapshots);
    db.Controllers.Remove(controller);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

// Join 映射支持脉冲发送与状态/使能反馈；定时任务复用同一发送服务。
api.MapPost("/controllers/{controllerId:guid}/joins", async (
    Guid controllerId,
    CreateJoinMappingRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
    {
        throw new ArgumentException("Join name is required and must be at most 120 characters.");
    }
    if (request.SendJoinNumber is < 1 or > 32768 ||
        request.StateJoinNumber is < 1 or > 32768 ||
        request.EnableJoinNumber is < 0 or > 32768)
    {
        throw new ArgumentException("Send and state Join numbers must be between 1 and 32768; enable Join must be 0 or between 1 and 32768.");
    }

    if (!await db.Controllers.AnyAsync(item => item.Id == controllerId, cancellationToken))
    {
        throw new KeyNotFoundException("Controller was not found.");
    }
    if (await db.JoinMappings.AnyAsync(item =>
            item.ControllerId == controllerId &&
            item.Kind == JoinKind.Digital &&
            item.SendJoinNumber == request.SendJoinNumber,
            cancellationToken))
    {
        throw new InvalidOperationException("That send Join is already mapped for this controller.");
    }

    var mapping = new JoinMapping
    {
        ControllerId = controllerId,
        Name = request.Name.Trim(),
        Kind = JoinKind.Digital,
        SendJoinNumber = request.SendJoinNumber,
        StateJoinNumber = request.StateJoinNumber,
        EnableJoinNumber = request.EnableJoinNumber ?? 0,
        ShowConfirmationPopup = request.ShowConfirmationPopup,
        // 新控制点排在当前列表末尾。
        SortOrder = (await db.JoinMappings
            .Where(item => item.ControllerId == controllerId)
            .MaxAsync(item => (int?)item.SortOrder, cancellationToken) ?? -1) + 1
    };
    db.JoinMappings.Add(mapping);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/joins/{mapping.Id}", new
    {
        mapping.Id,
        mapping.ControllerId,
        mapping.Name,
        mapping.SendJoinNumber,
        mapping.StateJoinNumber,
        mapping.EnableJoinNumber,
        mapping.ShowConfirmationPopup
    });
}).RequireAuthorization("AdminOnly");

api.MapPut("/joins/{mappingId:guid}", async (
    Guid mappingId,
    UpdateJoinMappingRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
    {
        throw new ArgumentException("Join name is required and must be at most 120 characters.");
    }
    if (request.SendJoinNumber is < 1 or > 32768 ||
        request.StateJoinNumber is < 1 or > 32768 ||
        request.EnableJoinNumber is < 0 or > 32768)
    {
        throw new ArgumentException("Send and state Join numbers must be between 1 and 32768; enable Join must be 0 or between 1 and 32768.");
    }

    var mapping = await db.JoinMappings.SingleOrDefaultAsync(item => item.Id == mappingId, cancellationToken)
        ?? throw new KeyNotFoundException("Join mapping was not found.");
    if (mapping.Kind != JoinKind.Digital)
    {
        throw new InvalidOperationException("Analog and serial control points are no longer supported.");
    }

    if (await db.JoinMappings.AnyAsync(item =>
            item.Id != mappingId &&
            item.ControllerId == mapping.ControllerId &&
            item.Kind == JoinKind.Digital &&
            item.SendJoinNumber == request.SendJoinNumber,
            cancellationToken))
    {
        throw new InvalidOperationException("That send Join is already mapped for this controller.");
    }

    mapping.Name = request.Name.Trim();
    mapping.SendJoinNumber = request.SendJoinNumber;
    mapping.StateJoinNumber = request.StateJoinNumber;
    mapping.EnableJoinNumber = request.EnableJoinNumber ?? 0;
    mapping.ShowConfirmationPopup = request.ShowConfirmationPopup;
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

api.MapPost("/joins/{mappingId:guid}/copy", async (
    Guid mappingId,
    CopyJoinMappingRequest request,
    ControlDbContext db,
    ScheduleService scheduleService,
    CancellationToken cancellationToken) =>
{
    var source = await db.JoinMappings
        .SingleOrDefaultAsync(item => item.Id == mappingId, cancellationToken)
        ?? throw new KeyNotFoundException("Join mapping was not found.");
    if (source.Kind != JoinKind.Digital)
    {
        throw new InvalidOperationException("Analog and serial control points are no longer supported.");
    }

    var targetIds = (request.ControllerIds ?? [])
        .Distinct()
        .Where(id => id != source.ControllerId)
        .ToList();
    if (targetIds.Count == 0)
    {
        throw new ArgumentException("Please select at least one other room.");
    }

    var targets = await db.Controllers
        .Include(controller => controller.Room)
        .Where(controller => targetIds.Contains(controller.Id))
        .ToListAsync(cancellationToken);
    if (targets.Count != targetIds.Count)
    {
        throw new KeyNotFoundException("A target controller was not found.");
    }

    foreach (var target in targets)
    {
        var conflicts = await db.JoinMappings
            .Where(item =>
                item.ControllerId == target.Id &&
                item.Kind == JoinKind.Digital &&
                item.SendJoinNumber == source.SendJoinNumber)
            .ToListAsync(cancellationToken);
        foreach (var conflict in conflicts)
        {
            await scheduleService.DeleteForJoinMappingAsync(conflict.Id, cancellationToken);
            db.JoinMappings.Remove(conflict);
        }

        var sortOrder = (await db.JoinMappings
            .Where(item => item.ControllerId == target.Id)
            .MaxAsync(item => (int?)item.SortOrder, cancellationToken) ?? -1) + 1;
        db.JoinMappings.Add(new JoinMapping
        {
            ControllerId = target.Id,
            Name = source.Name,
            Kind = JoinKind.Digital,
            SendJoinNumber = source.SendJoinNumber,
            StateJoinNumber = source.StateJoinNumber,
            EnableJoinNumber = source.EnableJoinNumber,
            ShowConfirmationPopup = source.ShowConfirmationPopup,
            SortOrder = sortOrder
        });
    }

    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { copiedCount = targets.Count });
}).RequireAuthorization("AdminOnly");

api.MapPost("/controllers/{controllerId:guid}/joins/copy-all", async (
    Guid controllerId,
    CopyAllJoinMappingsRequest request,
    ControlDbContext db,
    ScheduleService scheduleService,
    CancellationToken cancellationToken) =>
{
    var sourceMappings = await db.JoinMappings
        .Where(item => item.ControllerId == controllerId && item.Kind == JoinKind.Digital)
        .OrderBy(item => item.SortOrder)
        .ThenBy(item => item.Name)
        .ToListAsync(cancellationToken);
    if (sourceMappings.Count == 0)
    {
        throw new InvalidOperationException("当前教室没有可复制的控制点。");
    }

    var targetIds = (request.ControllerIds ?? [])
        .Distinct()
        .Where(id => id != controllerId)
        .ToList();
    if (targetIds.Count == 0)
    {
        throw new ArgumentException("Please select at least one other room.");
    }

    var targets = await db.Controllers
        .Include(controller => controller.Room)
        .Where(controller => targetIds.Contains(controller.Id))
        .ToListAsync(cancellationToken);
    if (targets.Count != targetIds.Count)
    {
        throw new KeyNotFoundException("A target controller was not found.");
    }

    foreach (var target in targets)
    {
        var existingMappings = await db.JoinMappings
            .Where(item => item.ControllerId == target.Id)
            .ToListAsync(cancellationToken);
        foreach (var existing in existingMappings)
        {
            await scheduleService.DeleteForJoinMappingAsync(existing.Id, cancellationToken);
            db.JoinMappings.Remove(existing);
        }

        for (var index = 0; index < sourceMappings.Count; index++)
        {
            var source = sourceMappings[index];
            db.JoinMappings.Add(new JoinMapping
            {
                ControllerId = target.Id,
                Name = source.Name,
                Kind = JoinKind.Digital,
                SendJoinNumber = source.SendJoinNumber,
                StateJoinNumber = source.StateJoinNumber,
                EnableJoinNumber = source.EnableJoinNumber,
                ShowConfirmationPopup = source.ShowConfirmationPopup,
                SortOrder = index
            });
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { copiedCount = targets.Count, controlPointCount = sourceMappings.Count });
}).RequireAuthorization("AdminOnly");

// 配置页拖拽排序后提交完整顺序，控制台按钮按此顺序渲染。
api.MapPut("/controllers/{controllerId:guid}/joins/order", async (
    Guid controllerId,
    ReorderJoinMappingsRequest request,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var mappings = await db.JoinMappings
        .Where(join => join.ControllerId == controllerId && join.Kind == JoinKind.Digital)
        .ToListAsync(cancellationToken);
    if (mappings.Count == 0)
    {
        throw new KeyNotFoundException("Controller has no control points.");
    }

    var requestedIds = request.MappingIds ?? [];
    if (requestedIds.Count != mappings.Count ||
        requestedIds.Distinct().Count() != mappings.Count ||
        requestedIds.Any(id => mappings.All(mapping => mapping.Id != id)))
    {
        throw new ArgumentException("The order must contain every control point exactly once.");
    }

    var byId = mappings.ToDictionary(mapping => mapping.Id);
    for (var index = 0; index < requestedIds.Count; index++)
    {
        byId[requestedIds[index]].SortOrder = index;
    }

    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

api.MapDelete("/joins/{mappingId:guid}", async (
    Guid mappingId,
    ControlDbContext db,
    ScheduleService scheduleService,
    CancellationToken cancellationToken) =>
{
    var mapping = await db.JoinMappings.SingleOrDefaultAsync(item => item.Id == mappingId, cancellationToken)
        ?? throw new KeyNotFoundException("Join mapping was not found.");

    await scheduleService.DeleteForJoinMappingAsync(mappingId, cancellationToken);
    db.JoinMappings.Remove(mapping);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

api.MapPost("/joins/{mappingId:guid}/pulse", async (
    Guid mappingId,
    ControllerConnectionService connectionService,
    CancellationToken cancellationToken) =>
{
    await connectionService.SendDigitalPulseAsync(mappingId, enforceEnableJoin: true, cancellationToken);
    return Results.Accepted();
}).RequireAuthorization("OperatorOrAdmin");

// 定时任务端点与 SignalR Hub 共用后台状态和角色授权策略。
api.MapGet("/schedules", async (ScheduleService service, CancellationToken cancellationToken) =>
{
    var schedules = await service.GetAllAsync(cancellationToken);
    return Results.Ok(schedules.Select(schedule => new
    {
        schedule.Id,
        schedule.GroupId,
        schedule.JoinMappingId,
        RoomId = schedule.JoinMapping?.Controller?.RoomId,
        schedule.RoomName,
        schedule.JoinName,
        schedule.Name,
        schedule.Enabled,
        schedule.Kind,
        schedule.RunAtUtc,
        schedule.WeekdayMask,
        schedule.LocalTime,
        schedule.NextRunAtUtc,
        JoinMapping = schedule.JoinMapping is null
            ? null
            : new { schedule.JoinMapping.Id, schedule.JoinMapping.Name }
    }));
});

api.MapPost("/schedules", async (
    ScheduleDefinitionRequest request,
    ScheduleService service,
    CancellationToken cancellationToken) =>
{
    var (name, runAtUtc) = ValidateScheduleDefinition(request);
    var schedules = await service.CreateGroupAsync(
        request.JoinMappingIds,
        name,
        request.Kind,
        runAtUtc,
        request.WeekdayMask,
        request.LocalTime,
        cancellationToken);
    var groupId = schedules[0].GroupId;
    return Results.Created($"/api/schedules/groups/{groupId}", new
    {
        GroupId = groupId,
        TargetIds = schedules.Select(schedule => schedule.Id).ToArray(),
        schedules[0].Name,
        schedules[0].Kind,
        schedules[0].NextRunAtUtc
    });
}).RequireAuthorization("OperatorOrAdmin");

api.MapPut("/schedules/groups/{groupId:guid}", async (
    Guid groupId,
    ScheduleDefinitionRequest request,
    ScheduleService service,
    CancellationToken cancellationToken) =>
{
    var (name, runAtUtc) = ValidateScheduleDefinition(request);
    var schedules = await service.UpdateGroupAsync(
        groupId,
        request.JoinMappingIds,
        name,
        request.Kind,
        runAtUtc,
        request.WeekdayMask,
        request.LocalTime,
        cancellationToken);
    return Results.Ok(new
    {
        GroupId = groupId,
        TargetIds = schedules.Select(schedule => schedule.Id).ToArray(),
        schedules[0].Name,
        schedules[0].Kind,
        schedules[0].NextRunAtUtc
    });
}).RequireAuthorization("OperatorOrAdmin");

api.MapDelete("/schedules/groups/{groupId:guid}", async (
    Guid groupId,
    ScheduleService service,
    CancellationToken cancellationToken) =>
{
    await service.DeleteGroupAsync(groupId, cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("OperatorOrAdmin");

api.MapDelete("/schedules/{scheduleId:guid}", async (
    Guid scheduleId,
    ScheduleService service,
    CancellationToken cancellationToken) =>
{
    await service.DeleteAsync(scheduleId, cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("OperatorOrAdmin");

// 暂停保留任务定义与执行记录；恢复时按当前时间重算下一次执行。
api.MapPost("/schedules/groups/{groupId:guid}/pause", async (
    Guid groupId,
    ScheduleService service,
    CancellationToken cancellationToken) =>
{
    await service.SetGroupEnabledAsync(groupId, false, cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("OperatorOrAdmin");

api.MapPost("/schedules/groups/{groupId:guid}/resume", async (
    Guid groupId,
    ScheduleService service,
    CancellationToken cancellationToken) =>
{
    await service.SetGroupEnabledAsync(groupId, true, cancellationToken);
    return Results.NoContent();
}).RequireAuthorization("OperatorOrAdmin");

// 执行记录只读，保留最近 100 条供自动任务页展开查看；任务删除后记录一同清除。
api.MapGet("/schedules/{scheduleId:guid}/executions", async (
    Guid scheduleId,
    ControlDbContext db,
    CancellationToken cancellationToken) =>
{
    var executions = await db.ScheduleExecutions.AsNoTracking()
        .Where(item => item.ScheduleId == scheduleId)
        .OrderByDescending(item => item.ScheduledAtUtc)
        .Take(100)
        .ToListAsync(cancellationToken);
    return Results.Ok(executions);
});

// SignalR 与 REST 共用 Cookie 会话；其余未匹配的 GET 交给单页应用入口。
app.MapHub<ControlHub>("/hubs/control").RequireAuthorization();
app.MapFallbackToFile("index.html");

// 创建与修改共用同一套校验，保证一次性/每周任务的字段一致性。
static (string Name, DateTimeOffset? RunAtUtc) ValidateScheduleDefinition(ScheduleDefinitionRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
    {
        throw new ArgumentException("Schedule name is required and must be at most 120 characters.");
    }
    if (!Enum.IsDefined(request.Kind))
    {
        throw new ArgumentException("Unsupported schedule kind.");
    }
    if (request.JoinMappingIds.Count == 0)
    {
        throw new ArgumentException("At least one control point is required.");
    }

    DateTimeOffset? runAtUtc = null;
    if (request.Kind == ScheduleKind.Once)
    {
        if (request.RunAtLocal is null)
        {
            throw new ArgumentException("A one-time schedule requires a local run time.");
        }

        // 项目只服务单一本地时区，提交的墙上时间直接按服务器本地时间解释。
        var localRunAt = DateTime.SpecifyKind(request.RunAtLocal.Value, DateTimeKind.Unspecified);
        runAtUtc = new DateTimeOffset(localRunAt);
        if (runAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("A one-time schedule must have a future run time.");
        }
    }
    if (request.Kind == ScheduleKind.Weekly &&
        (request.WeekdayMask is < 1 or > 127 ||
         request.LocalTime < TimeSpan.Zero ||
         request.LocalTime >= TimeSpan.FromDays(1)))
    {
        throw new ArgumentException("A weekly schedule requires a weekday mask and local time.");
    }

    return (request.Name.Trim(), runAtUtc);
}

static (string Name, string Building, string Floor) NormalizeRoom(CreateRoomRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
    {
        throw new ArgumentException("Room name is required and must be at most 120 characters.");
    }

    var building = request.Building?.Trim() ?? string.Empty;
    var floor = request.Floor?.Trim() ?? string.Empty;
    if (building.Length > 60 || floor.Length > 60)
    {
        throw new ArgumentException("Building and floor must be at most 60 characters.");
    }

    return (request.Name.Trim(), building, floor);
}

static async Task EnsureLocationAsync(
    ControlDbContext db,
    string building,
    string floor,
    CancellationToken cancellationToken)
{
    if (building.Length == 0 || floor.Length == 0)
    {
        throw new ArgumentException("请先在配置页选择楼栋与楼层。");
    }

    var exists = await db.Floors.AnyAsync(
        item => item.Name == floor && item.Building.Name == building,
        cancellationToken);
    if (!exists)
    {
        throw new ArgumentException("所选楼栋或楼层未在配置页中定义，请重新选择。");
    }
}

static string NormalizeLocationName(string? name, string label)
{
    var trimmed = name?.Trim() ?? string.Empty;
    if (trimmed.Length == 0 || trimmed.Length > 60)
    {
        throw new ArgumentException($"{label}名称必填且不能超过 60 个字符。");
    }

    return trimmed;
}

static async Task SeedLocationsAsync(ControlDbContext db, CancellationToken cancellationToken)
{
    // 升级旧数据库时，从已有教室字段补建楼栋/楼层选项；新库不预置空数据。
    if (await db.Buildings.AnyAsync(cancellationToken))
    {
        return;
    }

    var pairs = await db.Rooms.AsNoTracking()
        .Where(room => room.Building != "" && room.Floor != "")
        .Select(room => new { room.Building, room.Floor })
        .Distinct()
        .ToListAsync(cancellationToken);
    if (pairs.Count == 0)
    {
        return;
    }

    var buildings = new Dictionary<string, Building>();
    foreach (var pair in pairs.OrderBy(item => item.Building).ThenBy(item => item.Floor))
    {
        if (!buildings.TryGetValue(pair.Building, out var building))
        {
            building = new Building { Name = pair.Building };
            buildings[pair.Building] = building;
            db.Buildings.Add(building);
        }

        if (building.Floors.All(floor => floor.Name != pair.Floor))
        {
            building.Floors.Add(new Floor { Name = pair.Floor });
        }
    }

    await db.SaveChangesAsync(cancellationToken);
}

await app.RunAsync();

public partial class Program;
