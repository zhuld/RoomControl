using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomControl.Api.Data;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证多教室计划任务的创建、修改，以及目标减少后的删除行为。</summary>
[Collection(QuartzCollection.Name)]
public sealed class ScheduleGroupTests(QuartzSchedulerFixture quartz)
{
    [Fact]
    public async Task CreatingAndUpdatingGroupKeepsOneTargetPerRoom()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();

        var contextFactory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();
        Guid firstMappingId;
        Guid secondMappingId;
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var roomName in new[] { "A101", "A102" })
            {
                var room = new Room { Name = roomName };
                var controller = new Controller { Room = room, Name = $"{roomName}-CIP", Host = "localhost" };
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
            }

            await db.SaveChangesAsync();
            var mappings = await db.JoinMappings.OrderBy(item => item.Id).ToListAsync();
            firstMappingId = mappings[0].Id;
            secondMappingId = mappings[1].Id;
        }

        var service = quartz.CreateService(contextFactory);

        var created = await service.CreateGroupAsync(
            [firstMappingId, secondMappingId],
            "工作日开机",
            ScheduleKind.Weekly,
            null,
            0b0011111,
            new TimeSpan(8, 30, 0),
            "Asia/Shanghai",
            CancellationToken.None);

        Assert.Equal(2, created.Count);
        Assert.NotNull(created[0].GroupId);
        Assert.All(created, schedule => Assert.Equal(created[0].GroupId, schedule.GroupId));
        Assert.Equal(2, created.Select(schedule => schedule.JoinMappingId).Distinct().Count());

        var groupId = created[0].GroupId!.Value;
        var droppedId = created.Single(schedule => schedule.JoinMappingId == firstMappingId).Id;
        var keptId = created.Single(schedule => schedule.JoinMappingId == secondMappingId).Id;

        var updated = await service.UpdateGroupAsync(
            groupId,
            [secondMappingId],
            "工作日开机（已调整）",
            ScheduleKind.Weekly,
            null,
            0b0000011,
            new TimeSpan(9, 0, 0),
            "Asia/Shanghai",
            CancellationToken.None);

        Assert.Single(updated);
        Assert.Equal(keptId, updated[0].Id);
        Assert.Equal("工作日开机（已调整）", updated[0].Name);
        Assert.Equal(new TimeSpan(9, 0, 0), updated[0].LocalTime);
        Assert.Equal(0b0000011, updated[0].WeekdayMask);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            Assert.False(await db.Schedules.AnyAsync(item => item.Id == droppedId));

            var kept = await db.Schedules.SingleAsync(item => item.Id == keptId);
            Assert.True(kept.Enabled);
            Assert.Equal(secondMappingId, kept.JoinMappingId);
            Assert.Equal(1, await db.Schedules.CountAsync(item => item.GroupId == groupId));
        }
    }
}
