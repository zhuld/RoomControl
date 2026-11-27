using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomControl.Api.Contracts;
using RoomControl.Api.Data;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证配置导出内容完整、导入整体替换配置，并保留账号与操作日志。</summary>
[Collection(QuartzCollection.Name)]
public sealed class ConfigTransferTests(QuartzSchedulerFixture quartz)
{
    [Fact]
    public async Task ExportThenImportReplacesConfigurationAndKeepsUsersAndLogs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();
        var contextFactory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.SiteSettings.Add(new SiteSettings { DisplayName = "主楼站点", Subtitle = "备份测试" });
            db.Users.Add(new User { Username = "operator", Role = UserRole.Operator });
            db.AuditLogs.Add(new AuditLog
            {
                Category = AuditLogCategories.Configuration,
                Action = "测试日志",
                Actor = "tester"
            });

            var building = new Building { Name = "A 栋" };
            building.Floors.Add(new Floor { Name = "1F" });
            db.Buildings.Add(building);

            foreach (var roomName in new[] { "A101", "A102" })
            {
                var room = new Room { Name = roomName, Building = "A 栋", Floor = "1F" };
                var controller = new Controller
                {
                    Room = room,
                    Host = $"10.0.0.{roomName[^1]}",
                    Port = 41794,
                    Ipid = 3
                };
                controller.JoinMappings.Add(new JoinMapping
                {
                    Name = "开机",
                    Kind = JoinKind.Digital,
                    SendJoinNumber = 1,
                    StateJoinNumber = 2,
                    EnableJoinNumber = 0,
                    SortOrder = 0
                });
                room.Controller = controller;
                db.Rooms.Add(room);
            }

            await db.SaveChangesAsync();
            var mappingIds = await db.JoinMappings.OrderBy(item => item.Name)
                .Select(item => item.Id)
                .ToListAsync();
            var manualTask = new ManualTask { Name = "一键开机", JoinName = "开机", SortOrder = 0 };
            manualTask.Targets.Add(new ManualTaskTarget
            {
                JoinMappingId = mappingIds[0],
                RoomName = "A101",
                JoinName = "开机"
            });
            manualTask.Targets.Add(new ManualTaskTarget
            {
                JoinMappingId = mappingIds[1],
                RoomName = "A102",
                JoinName = "开机"
            });
            db.ManualTasks.Add(manualTask);
            await db.SaveChangesAsync();
        }

        var scheduleService = quartz.CreateService(contextFactory);
        var transfer = quartz.CreateConfigTransferService(contextFactory, scheduleService);
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await scheduleService.CreateGroupAsync(
                await db.JoinMappings.Select(item => item.Id).ToListAsync(),
                "工作日开机",
                ScheduleKind.Weekly,
                null,
                0b0011111,
                new TimeSpan(8, 0, 0),
                CancellationToken.None);
        }

        var document = await transfer.ExportAsync(CancellationToken.None);
        Assert.Equal(ConfigDocumentVersion.Current, document.Version);
        Assert.Equal("主楼站点", document.Branding?.DisplayName);
        Assert.Equal(2, document.Rooms!.Count);
        Assert.All(document.Rooms, room => Assert.Single(room.Controller!.Joins!));
        Assert.Single(document.Schedules!);
        Assert.Equal(2, document.Schedules![0].Targets!.Count);
        Assert.Single(document.ManualTasks!);
        Assert.Equal(2, document.ManualTasks![0].Targets!.Count);

        // 导入前改动数据，确认导出的文件内容会覆盖这些改动。
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            db.Rooms.Add(new Room { Name = "临时教室", Building = "A 栋", Floor = "1F" });
            var settings = await db.SiteSettings.SingleAsync();
            settings.DisplayName = "被覆盖的名称";
            db.AuditLogs.Add(new AuditLog
            {
                Category = AuditLogCategories.Configuration,
                Action = "导入后的日志",
                Actor = "tester"
            });
            await db.SaveChangesAsync();
        }

        var summary = await transfer.ReplaceAsync(document, CancellationToken.None);
        Assert.Equal(2, summary.RoomCount);
        Assert.Equal(2, summary.ControllerCount);
        Assert.Equal(2, summary.JoinCount);
        Assert.Equal(2, summary.ScheduleCount);
        Assert.Equal(1, summary.ManualTaskCount);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var rooms = await db.Rooms.Include(room => room.Controller)
                .ThenInclude(controller => controller!.JoinMappings)
                .OrderBy(room => room.Name)
                .ToListAsync();
            Assert.Equal(["A101", "A102"], rooms.Select(room => room.Name));
            Assert.All(rooms, room => Assert.Single(room.Controller!.JoinMappings));
            Assert.Equal("10.0.0.1", rooms[0].Controller!.Host);

            var schedules = await db.Schedules.ToListAsync();
            Assert.Equal(2, schedules.Count);
            Assert.All(schedules, schedule => Assert.Equal("工作日开机", schedule.Name));
            Assert.Single(schedules.Select(schedule => schedule.GroupId).Distinct());

            var manualTask = await db.ManualTasks.Include(task => task.Targets).SingleAsync();
            Assert.Equal("一键开机", manualTask.Name);
            Assert.Equal(2, manualTask.Targets.Count);

            Assert.Equal("主楼站点", (await db.SiteSettings.SingleAsync()).DisplayName);
            // 账号与操作日志不参与导入，导入前后的记录都保留。
            Assert.Equal("operator", (await db.Users.SingleAsync()).Username);
            Assert.Equal(2, await db.AuditLogs.CountAsync());
        }
    }

    [Fact]
    public async Task ImportRejectsUnknownControlPointWithoutChangingData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();
        var contextFactory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101", Building = "A 栋", Floor = "1F" };
            room.Controller = new Controller { Room = room, Host = "10.0.0.1" };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
        }

        var transfer = quartz.CreateConfigTransferService(
            contextFactory,
            quartz.CreateService(contextFactory));
        var document = new ConfigDocument(
            ConfigDocumentVersion.Current,
            DateTimeOffset.UtcNow,
            new ConfigBranding("站点", string.Empty, null),
            [new ConfigBuilding("A 栋", ["1F"])],
            [new ConfigRoom("A101", "A 栋", "1F", null)],
            [
                new ConfigSchedule(
                    "坏任务",
                    ScheduleKind.Weekly,
                    null,
                    0b0000001,
                    new TimeSpan(8, 0, 0),
                    true,
                    [new ConfigTarget("A 栋", "1F", "不存在的教室", "开机")])
            ],
            []);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => transfer.ReplaceAsync(document, CancellationToken.None));

        Assert.Contains("不存在的教室", error.Message);
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            // 校验失败发生在写库前，原配置保持不变。
            Assert.Equal("A101", (await db.Rooms.SingleAsync()).Name);
            Assert.Empty(await db.Schedules.ToListAsync());
        }
    }

    [Fact]
    public async Task ImportRejectsUnsupportedDocumentVersion()
    {
        var transfer = quartz.CreateConfigTransferService(
            new StubDbContextFactory(),
            quartz.CreateService(new StubDbContextFactory()));

        var document = new ConfigDocument(
            99,
            DateTimeOffset.UtcNow,
            new ConfigBranding("站点", string.Empty, null),
            [],
            [],
            [],
            []);

        await Assert.ThrowsAsync<ArgumentException>(
            () => transfer.ReplaceAsync(document, CancellationToken.None));
    }

    private sealed class StubDbContextFactory : IDbContextFactory<ControlDbContext>
    {
        public ControlDbContext CreateDbContext() =>
            throw new InvalidOperationException("版本校验应先于数据库访问。");

        public Task<ControlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("版本校验应先于数据库访问。");
    }
}
