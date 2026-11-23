using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>日志中的操作对象：可读名称，以及 Join 编号等操作细节。</summary>
public sealed record AuditTarget(string Label, string? Detail = null);

/// <summary>把路由与响应中的对象 ID 解析成可读名称，并带上所属教室与 Join 编号。</summary>
public sealed class AuditLogTargetResolver(IDbContextFactory<ControlDbContext> dbContextFactory)
{
    public async Task<AuditTarget?> ResolveAsync(HttpContext context)
    {
        var values = context.Request.RouteValues;
        if (TryGetId(values, "mappingId", out var mappingId))
        {
            return await ResolveAsync("joins", mappingId);
        }
        if (TryGetId(values, "scheduleId", out var scheduleId))
        {
            return await ResolveAsync("schedules", scheduleId);
        }
        if (TryGetId(values, "groupId", out var groupId))
        {
            return await ResolveScheduleGroupAsync(groupId);
        }
        if (TryGetId(values, "controllerId", out var controllerId))
        {
            return await ResolveAsync("controllers", controllerId);
        }
        if (TryGetId(values, "roomId", out var roomId))
        {
            return await ResolveAsync("rooms", roomId);
        }
        if (TryGetId(values, "buildingId", out var buildingId))
        {
            return await ResolveAsync("buildings", buildingId);
        }
        if (TryGetId(values, "floorId", out var floorId))
        {
            return await ResolveAsync("floors", floorId);
        }
        if (TryGetId(values, "id", out var userId))
        {
            return await ResolveAsync("users", userId);
        }
        return null;
    }

    /// <summary>创建接口通过 Location 头返回新对象地址，据此补全日志对象。</summary>
    public async Task<AuditTarget?> ResolveLocationAsync(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }
        if (!Uri.TryCreate(location, UriKind.RelativeOrAbsolute, out var uri))
        {
            return null;
        }

        var segments = (uri.IsAbsoluteUri ? uri.AbsolutePath : location)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        // 期望形如 /api/{resource}/{id}
        if (segments.Length < 3 || !Guid.TryParse(segments[^1], out var id))
        {
            return null;
        }
        return await ResolveAsync(segments[^2].ToLowerInvariant(), id);
    }

    private async Task<AuditTarget?> ResolveAsync(string resource, Guid id)
    {        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        switch (resource)
        {
            case "joins":
            {
                // 控制点日志同时记录所属教室和 Join 编号，便于定位是哪个教室的哪条指令。
                var join = await db.JoinMappings.AsNoTracking()
                    .Where(item => item.Id == id)
                    .Select(item => new { item.Name, item.SendJoinNumber, Room = item.Controller.Room })
                    .SingleOrDefaultAsync();
                return join is null
                    ? new AuditTarget($"控制点 {id}")
                    : new AuditTarget(
                        $"{AuditLogFormatting.FormatRoom(join.Room)}· 控制点「{join.Name}」",
                        $"Join {join.SendJoinNumber}");
            }
            case "schedules":
            {
                var schedule = await db.Schedules.AsNoTracking()
                    .Where(item => item.Id == id)
                    .Select(item => new { item.Name, item.RoomName })
                    .SingleOrDefaultAsync();
                return schedule is null
                    ? new AuditTarget($"定时任务 {id}")
                    : new AuditTarget($"教室「{schedule.RoomName}」· 定时任务「{schedule.Name}」");
            }
            case "controllers":
            {
                var controller = await db.Controllers.AsNoTracking()
                    .Where(item => item.Id == id)
                    .Select(item => new { item.Name, Room = item.Room })
                    .SingleOrDefaultAsync();
                return controller is null
                    ? new AuditTarget($"中控 {id}")
                    : new AuditTarget(
                        $"{AuditLogFormatting.FormatRoom(controller.Room)}· 中控「{controller.Name}」");
            }
            case "rooms":
            {
                var room = await db.Rooms.AsNoTracking()
                    .Where(item => item.Id == id)
                    .SingleOrDefaultAsync();
                return room is null
                    ? new AuditTarget($"教室 {id}")
                    : new AuditTarget(AuditLogFormatting.FormatRoom(room));
            }
            case "buildings":
            {
                var name = await db.Buildings.AsNoTracking()
                    .Where(item => item.Id == id)
                    .Select(item => item.Name)
                    .SingleOrDefaultAsync();
                return name is null ? new AuditTarget($"楼栋 {id}") : new AuditTarget($"楼栋「{name}」");
            }
            case "floors":
            {
                var floor = await db.Floors.AsNoTracking()
                    .Where(item => item.Id == id)
                    .Select(item => new { item.Name, BuildingName = item.Building.Name })
                    .SingleOrDefaultAsync();
                return floor is null
                    ? new AuditTarget($"楼层 {id}")
                    : new AuditTarget($"楼层「{floor.BuildingName} · {floor.Name}」");
            }
            case "users":
            {
                var username = await db.Users.AsNoTracking()
                    .Where(item => item.Id == id)
                    .Select(item => item.Username)
                    .SingleOrDefaultAsync();
                return username is null ? new AuditTarget($"账号 {id}") : new AuditTarget($"账号「{username}」");
            }
            default:
                return null;
        }
    }

    /// <summary>计划任务按组操作时，用组内任一目标拼出任务名与教室范围。</summary>
    private async Task<AuditTarget?> ResolveScheduleGroupAsync(Guid groupId)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        var schedules = await db.Schedules.AsNoTracking()
            .Where(item => item.GroupId == groupId || (item.GroupId == null && item.Id == groupId))
            .Select(item => new { item.Name, item.RoomName })
            .ToListAsync();
        if (schedules.Count == 0)
        {
            return new AuditTarget($"定时任务 {groupId}");
        }

        var first = schedules[0];
        var scope = schedules.Count > 1
            ? $"教室「{first.RoomName}」等 {schedules.Count} 间"
            : $"教室「{first.RoomName}」";
        return new AuditTarget($"{scope} · 定时任务「{first.Name}」");
    }

    private static bool TryGetId(RouteValueDictionary values, string key, out Guid id)
    {
        id = Guid.Empty;
        return values.TryGetValue(key, out var raw) &&
            raw is string text &&
            Guid.TryParse(text, out id);
    }
}
