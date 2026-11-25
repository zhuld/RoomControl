using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证审计日志能把路由中的对象 ID 转成可读名称。</summary>
public sealed class AuditLogTargetResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsJoinNameForMappingRouteValue()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        var joinId = Guid.NewGuid();
        await using (var db = new ControlDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "测试教室", Building = "A楼", Floor = "1F" };
            // 控制点日志需要带上所属教室，避免只显示控制点名称。
            var controller = new Controller { Room = room, Host = "127.0.0.1", Port = 41794, Ipid = 3 };
            db.Rooms.Add(room);
            db.Controllers.Add(controller);
            db.JoinMappings.Add(new JoinMapping
            {
                Id = joinId,
                Controller = controller,
                Name = "上课",
                SendJoinNumber = 1,
                StateJoinNumber = 2,
            });
            await db.SaveChangesAsync();
        }

        var resolver = new AuditLogTargetResolver(new TestDbContextFactory(options));
        var context = new DefaultHttpContext();
        context.Request.RouteValues["mappingId"] = joinId.ToString();

        var target = await resolver.ResolveAsync(context);

        Assert.Equal("教室「A楼 · 1F · 测试教室」· 控制点「上课」", target?.Label);
        Assert.Equal("Join 1", target?.Detail);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToGuidWhenEntityIsMissing()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new ControlDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var resolver = new AuditLogTargetResolver(new TestDbContextFactory(options));
        var context = new DefaultHttpContext();
        var roomId = Guid.NewGuid();
        context.Request.RouteValues["roomId"] = roomId.ToString();

        Assert.Equal($"教室 {roomId}", (await resolver.ResolveAsync(context))?.Label);
    }

    [Fact]
    public async Task ResolveLocationAsync_ReturnsNameOfCreatedRoom()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        var roomId = Guid.NewGuid();
        await using (var db = new ControlDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Rooms.Add(new Room { Id = roomId, Name = "A101", Building = "A楼", Floor = "1F" });
            await db.SaveChangesAsync();
        }

        var resolver = new AuditLogTargetResolver(new TestDbContextFactory(options));

        Assert.Equal("教室「A楼 · 1F · A101」", (await resolver.ResolveLocationAsync($"/api/rooms/{roomId}"))?.Label);
        Assert.Equal("教室「A楼 · 1F · A101」", (await resolver.ResolveLocationAsync($"http://localhost/api/rooms/{roomId}"))?.Label);
        Assert.Null(await resolver.ResolveLocationAsync("/api/rooms"));
    }

    [Fact]
    public async Task ResolveLocationAsync_ReturnsScheduleGroupTarget()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        var groupId = Guid.NewGuid();
        await using (var db = new ControlDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Schedules.Add(new ControlSchedule
            {
                GroupId = groupId,
                Name = "上课铃",
                RoomName = "A101",
                Kind = ScheduleKind.Weekly,
                WeekdayMask = 127,
                LocalTime = TimeSpan.FromHours(8),
            });
            await db.SaveChangesAsync();
        }

        var resolver = new AuditLogTargetResolver(new TestDbContextFactory(options));

        Assert.Equal(
            "教室「A101」 · 自动任务「上课铃」",
            (await resolver.ResolveLocationAsync($"/api/schedules/groups/{groupId}"))?.Label);
    }

    [Theory]
    [InlineData("POST", "/api/joins/{mappingId:guid}/pulse", "Join 5", "发送 Join 5")]
    [InlineData("POST", "/api/joins/{mappingId:guid}/copy", "Join 3", "复制控制点到教室 Join 3")]
    [InlineData("POST", "/api/controllers/{controllerId:guid}/joins/copy-all", null, "复制全部控制点到教室")]
    [InlineData("PUT", "/api/joins/{mappingId}", "Join 3", "更新控制点 Join 3")]
    [InlineData("POST", "/api/users", null, "创建账号")]
    [InlineData("DELETE", "/api/unknown/{id:guid}", null, "DELETE /api/unknown/{id}")]
    public void ResolveAction_UsesReadableLabelAndJoinNumber(
        string method,
        string route,
        string? detail,
        string expected) =>
        Assert.Equal(expected, AuditLogActions.Resolve(method, route, detail));

    private sealed class TestDbContextFactory(DbContextOptions<ControlDbContext> options)
        : IDbContextFactory<ControlDbContext>
    {
        public ControlDbContext CreateDbContext() => new(options);
    }
}
