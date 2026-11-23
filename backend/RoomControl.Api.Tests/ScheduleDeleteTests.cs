using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomControl.Api.Data;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证删除控制点时任务与执行记录一并删除，以及暂停、恢复与删除任务组的行为。</summary>
[Collection(QuartzCollection.Name)]
public sealed class ScheduleDeleteTests(QuartzSchedulerFixture quartz)
{
    [Fact]
    public async Task DeletingJoinRemovesScheduleAndExecutions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();

        var contextFactory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();
        Guid mappingId;
        Guid scheduleId;
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();

            var room = new Room { Name = "A101" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost" };
            room.Controller = controller;
            var mapping = new JoinMapping
            {
                Controller = controller,
                Name = "Main light",
                Kind = JoinKind.Digital,
                SendJoinNumber = 1,
                StateJoinNumber = 2,
                EnableJoinNumber = 3
            };
            controller.JoinMappings.Add(mapping);

            var schedule = new ControlSchedule
            {
                JoinMapping = mapping,
                JoinMappingId = mapping.Id,
                RoomName = room.Name,
                JoinName = mapping.Name,
                Name = "Turn light on",
                Kind = ScheduleKind.Weekly
            };
            db.Rooms.Add(room);
            db.Schedules.Add(schedule);
            db.ScheduleExecutions.Add(new ScheduleExecution
            {
                Schedule = schedule,
                ScheduledAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Status = "Succeeded"
            });

            await db.SaveChangesAsync();
            mappingId = mapping.Id;
            scheduleId = schedule.Id;
        }

        var service = quartz.CreateService(contextFactory);
        await service.DeleteForJoinMappingAsync(mappingId, CancellationToken.None);

        Assert.Empty(await service.GetAllAsync(CancellationToken.None));

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            Assert.False(await db.Schedules.AnyAsync(item => item.Id == scheduleId));
            Assert.False(await db.ScheduleExecutions.AnyAsync(item => item.ScheduleId == scheduleId));

            var mapping = await db.JoinMappings.SingleAsync(item => item.Id == mappingId);
            db.JoinMappings.Remove(mapping);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task PausingAndResumingGroupTogglesEnabledAndNextRun()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();

        var contextFactory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();
        Guid mappingId;
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost" };
            room.Controller = controller;
            controller.JoinMappings.Add(new JoinMapping
            {
                Controller = controller,
                Name = "开机",
                Kind = JoinKind.Digital,
                SendJoinNumber = 1,
                StateJoinNumber = 2
            });
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            mappingId = await db.JoinMappings.Select(item => item.Id).SingleAsync();
        }

        var service = quartz.CreateService(contextFactory);
        var created = await service.CreateGroupAsync(
            [mappingId],
            "工作日开机",
            ScheduleKind.Weekly,
            null,
            0b0011111,
            new TimeSpan(8, 30, 0),
            "Asia/Shanghai",
            CancellationToken.None);
        var groupId = created[0].GroupId!.Value;
        Assert.True(created[0].Enabled);

        await service.SetGroupEnabledAsync(groupId, false, CancellationToken.None);
        var paused = Assert.Single(await service.GetAllAsync(CancellationToken.None));
        Assert.False(paused.Enabled);
        Assert.Null(paused.NextRunAtUtc);

        await service.SetGroupEnabledAsync(groupId, true, CancellationToken.None);
        var resumed = Assert.Single(await service.GetAllAsync(CancellationToken.None));
        Assert.True(resumed.Enabled);
        Assert.NotNull(resumed.NextRunAtUtc);

        await service.DeleteGroupAsync(groupId, CancellationToken.None);
        Assert.Empty(await service.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UpdatingPausedGroupKeepsItPaused()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();

        var contextFactory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();
        Guid mappingId;
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost" };
            room.Controller = controller;
            controller.JoinMappings.Add(new JoinMapping
            {
                Controller = controller,
                Name = "开机",
                Kind = JoinKind.Digital,
                SendJoinNumber = 1,
                StateJoinNumber = 2
            });
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            mappingId = await db.JoinMappings.Select(item => item.Id).SingleAsync();
        }

        var service = quartz.CreateService(contextFactory);
        var created = await service.CreateGroupAsync(
            [mappingId],
            "工作日开机",
            ScheduleKind.Weekly,
            null,
            0b0011111,
            new TimeSpan(8, 30, 0),
            "Asia/Shanghai",
            CancellationToken.None);
        var groupId = created[0].GroupId!.Value;
        await service.SetGroupEnabledAsync(groupId, false, CancellationToken.None);

        var updated = await service.UpdateGroupAsync(
            groupId,
            [mappingId],
            "工作日开机（已调整）",
            ScheduleKind.Weekly,
            null,
            0b0011111,
            new TimeSpan(9, 0, 0),
            "Asia/Shanghai",
            CancellationToken.None);

        var schedule = Assert.Single(updated);
        Assert.Equal("工作日开机（已调整）", schedule.Name);
        Assert.False(schedule.Enabled);
        Assert.Null(schedule.NextRunAtUtc);
    }

    [Fact]
    public async Task ResumingExpiredOneTimeScheduleIsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();

        var contextFactory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();
        Guid groupId;
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost" };
            room.Controller = controller;
            var mapping = new JoinMapping
            {
                Controller = controller,
                Name = "开机",
                Kind = JoinKind.Digital,
                SendJoinNumber = 1,
                StateJoinNumber = 2
            };
            controller.JoinMappings.Add(mapping);
            groupId = Guid.NewGuid();
            db.Rooms.Add(room);
            db.Schedules.Add(new ControlSchedule
            {
                GroupId = groupId,
                JoinMapping = mapping,
                JoinMappingId = mapping.Id,
                RoomName = room.Name,
                JoinName = mapping.Name,
                Name = "一次性开机",
                Kind = ScheduleKind.Once,
                Enabled = false,
                RunAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
        }

        var service = quartz.CreateService(contextFactory);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetGroupEnabledAsync(groupId, true, CancellationToken.None));
        Assert.Contains("一次性执行时间已过", error.Message);
    }
}
