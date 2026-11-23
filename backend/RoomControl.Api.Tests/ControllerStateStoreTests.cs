using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RoomControl.Api.Data;
using RoomControl.Api.Hubs;
using RoomControl.Api.Integrations;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证中控重连和状态回放时快照的保留、重置与纠正规则。</summary>
public sealed class ControllerStateStoreTests
{
    [Fact]
    public async Task ReconnectingResetsEnableStateToLowUntilThatJoinReportsAgain()
    {
        // 普通状态保留最后值，但使能状态在设备重新上报高电平前必须按低电平处理。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        var contextFactory = new TestContextFactory(options);
        Guid controllerId;

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost" };
            room.Controller = controller;
            controller.JoinMappings.Add(new JoinMapping
            {
                Controller = controller,
                Name = "Main light",
                Kind = JoinKind.Digital,
                SendJoinNumber = 5,
                StateJoinNumber = 6,
                EnableJoinNumber = 7
            });
            db.Rooms.Add(room);
            db.JoinSnapshots.AddRange(
                new JoinSnapshot
                {
                    ControllerId = controller.Id,
                    Kind = JoinKind.Digital,
                    JoinNumber = 6,
                    DigitalValue = true,
                    LastReportedAtUtc = DateTimeOffset.UtcNow
                },
                new JoinSnapshot
                {
                    ControllerId = controller.Id,
                    Kind = JoinKind.Digital,
                    JoinNumber = 7,
                    DigitalValue = true,
                    LastReportedAtUtc = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
            controllerId = controller.Id;
        }

        var store = new ControllerStateStore(
            contextFactory,
            new TestHubContext(),
            NullLogger<ControllerStateStore>.Instance);
        await store.SetConnectionAsync(controllerId, true, null, CancellationToken.None);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var status = await db.JoinSnapshots.SingleAsync(item => item.JoinNumber == 6);
            var enable = await db.JoinSnapshots.SingleAsync(item => item.JoinNumber == 7);
            Assert.True(status.DigitalValue);
            Assert.False(enable.DigitalValue);
        }

        await store.SaveJoinUpdateAsync(controllerId, new AdapterJoinUpdate(7, true), CancellationToken.None);
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var enable = await db.JoinSnapshots.SingleAsync(item => item.JoinNumber == 7);
            Assert.True(enable.DigitalValue);
        }
    }

    [Fact]
    public async Task ReconcileStateSnapshots_ClearsUnreportedStateJoinsAndKeepsEnableJoins()
    {
        // 状态回放结束时，只清除本轮未上报的高电平状态 Join。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        var contextFactory = new TestContextFactory(options);
        Guid controllerId;

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost" };
            room.Controller = controller;
            controller.JoinMappings.Add(new JoinMapping
            {
                Controller = controller,
                Name = "Main light",
                Kind = JoinKind.Digital,
                SendJoinNumber = 5,
                StateJoinNumber = 6,
                EnableJoinNumber = 7
            });
            db.Rooms.Add(room);
            db.JoinSnapshots.AddRange(
                new JoinSnapshot
                {
                    ControllerId = controller.Id,
                    Kind = JoinKind.Digital,
                    JoinNumber = 6,
                    DigitalValue = true,
                    LastReportedAtUtc = DateTimeOffset.UtcNow
                },
                new JoinSnapshot
                {
                    ControllerId = controller.Id,
                    Kind = JoinKind.Digital,
                    JoinNumber = 7,
                    DigitalValue = true,
                    LastReportedAtUtc = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
            controllerId = controller.Id;
        }

        var store = new ControllerStateStore(
            contextFactory,
            new TestHubContext(),
            NullLogger<ControllerStateStore>.Instance);
        await store.ReconcileStateSnapshotsAsync(controllerId, [], CancellationToken.None);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var state = await db.JoinSnapshots.SingleAsync(item => item.JoinNumber == 6);
            var enable = await db.JoinSnapshots.SingleAsync(item => item.JoinNumber == 7);
            Assert.False(state.DigitalValue);
            Assert.True(enable.DigitalValue);
        }

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var state = await db.JoinSnapshots.SingleAsync(item => item.JoinNumber == 6);
            state.DigitalValue = true;
            await db.SaveChangesAsync();
        }

        await store.ReconcileStateSnapshotsAsync(controllerId, [6], CancellationToken.None);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var state = await db.JoinSnapshots.SingleAsync(item => item.JoinNumber == 6);
            Assert.True(state.DigitalValue);
        }
    }

    [Fact]
    public async Task DisconnectingOnlineController_RecordsOnlyOneAuditEntry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        var contextFactory = new TestContextFactory(options);
        Guid controllerId;

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101", Building = "A楼", Floor = "1F" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost", IsOnline = true };
            room.Controller = controller;
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            controllerId = controller.Id;
        }

        var store = new ControllerStateStore(
            contextFactory,
            new TestHubContext(),
            NullLogger<ControllerStateStore>.Instance);
        await store.SetConnectionAsync(controllerId, false, "连接已停止。", CancellationToken.None);
        await store.SetConnectionAsync(controllerId, false, "连接已停止。", CancellationToken.None);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var entry = await db.AuditLogs.SingleAsync();
            Assert.Equal(AuditLogCategories.Connection, entry.Category);
            Assert.Equal("中控连接断开", entry.Action);
            Assert.Equal("教室「A楼 · 1F · A101」· 中控「CIP」", entry.Target);
            Assert.Equal("系统", entry.Actor);
        }
    }

    [Fact]
    public async Task ReconnectingController_RecordsConnectionAuditEntriesOnEachTransition()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        var contextFactory = new TestContextFactory(options);
        Guid controllerId;

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101", Building = "A楼", Floor = "1F" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost" };
            room.Controller = controller;
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            controllerId = controller.Id;
        }

        var store = new ControllerStateStore(
            contextFactory,
            new TestHubContext(),
            NullLogger<ControllerStateStore>.Instance);
        // 重复上报同一状态不应重复记录，只在在线状态真正切换时写连接日志。
        await store.SetConnectionAsync(controllerId, true, null, CancellationToken.None);
        await store.SetConnectionAsync(controllerId, false, "连接已停止。", CancellationToken.None);
        await store.SetConnectionAsync(controllerId, false, "连接已停止。", CancellationToken.None);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var entries = await db.AuditLogs.OrderBy(entry => entry.OccurredAtUtc).ToListAsync();
            Assert.Equal(2, entries.Count);
            Assert.All(entries, entry => Assert.Equal(AuditLogCategories.Connection, entry.Category));
            Assert.Contains(entries, entry => entry.Action == "中控连接成功");
            Assert.Contains(entries, entry => entry.Action == "中控连接断开");
        }
    }

    [Fact]
    public async Task SaveJoinUpdate_RecordsAuditEntryOnlyWhenDigitalValueChanges()
    {
        // 设备上报导致的状态变化写审计日志；相同电平重复上报不重复记录。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        var contextFactory = new TestContextFactory(options);
        Guid controllerId;

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101", Building = "A楼", Floor = "1F" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost" };
            room.Controller = controller;
            controller.JoinMappings.Add(new JoinMapping
            {
                Controller = controller,
                Name = "Main light",
                Kind = JoinKind.Digital,
                SendJoinNumber = 5,
                StateJoinNumber = 6,
            });
            db.Rooms.Add(room);
            db.JoinSnapshots.Add(new JoinSnapshot
            {
                ControllerId = controller.Id,
                Kind = JoinKind.Digital,
                JoinNumber = 6,
                DigitalValue = false,
                LastReportedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            controllerId = controller.Id;
        }

        var store = new ControllerStateStore(
            contextFactory,
            new TestHubContext(),
            NullLogger<ControllerStateStore>.Instance);
        await store.SaveJoinUpdateAsync(controllerId, new AdapterJoinUpdate(6, true), CancellationToken.None);
        await store.SaveJoinUpdateAsync(controllerId, new AdapterJoinUpdate(6, true), CancellationToken.None);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var entry = await db.AuditLogs.SingleAsync();
            Assert.Equal(AuditLogCategories.State, entry.Category);
            Assert.Equal("状态变更 Join 6：低电平 → 高电平", entry.Action);
            Assert.Equal("教室「A楼 · 1F · A101」· 控制点「Main light」", entry.Target);
            Assert.Equal("设备", entry.Actor);
        }
    }

    [Fact]
    public async Task SaveJoinUpdate_FirstLowReportDoesNotRecordAuditEntry()
    {
        // 未上报过的 Join 按低电平处理，首次低电平回报不产生状态变化日志。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        var contextFactory = new TestContextFactory(options);
        Guid controllerId;

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost" };
            room.Controller = controller;
            controller.JoinMappings.Add(new JoinMapping
            {
                Controller = controller,
                Name = "Main light",
                Kind = JoinKind.Digital,
                SendJoinNumber = 5,
                StateJoinNumber = 6,
            });
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            controllerId = controller.Id;
        }

        var store = new ControllerStateStore(
            contextFactory,
            new TestHubContext(),
            NullLogger<ControllerStateStore>.Instance);
        await store.SaveJoinUpdateAsync(controllerId, new AdapterJoinUpdate(6, false), CancellationToken.None);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            Assert.Empty(await db.AuditLogs.ToListAsync());
            var snapshot = await db.JoinSnapshots.SingleAsync(item => item.JoinNumber == 6);
            Assert.False(snapshot.DigitalValue);
        }
    }

    [Fact]
    public async Task StartupResetsPersistedStateSnapshotsToLow()
    {
        // 进程启动后设备尚未回报，数据库里残留的高电平不能继续显示，统一回落为低电平。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControlDbContext>()
            .UseSqlite(connection)
            .Options;
        var contextFactory = new TestContextFactory(options);
        Guid controllerId;

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101" };
            var controller = new Controller { Room = room, Name = "CIP", Host = "localhost", IsOnline = true };
            room.Controller = controller;
            db.Rooms.Add(room);
            db.JoinSnapshots.AddRange(
                new JoinSnapshot
                {
                    ControllerId = controller.Id,
                    Kind = JoinKind.Digital,
                    JoinNumber = 6,
                    DigitalValue = true,
                    LastReportedAtUtc = DateTimeOffset.UtcNow
                },
                new JoinSnapshot
                {
                    ControllerId = controller.Id,
                    Kind = JoinKind.Digital,
                    JoinNumber = 7,
                    DigitalValue = false,
                    LastReportedAtUtc = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
            controllerId = controller.Id;
        }

        var store = new ControllerStateStore(
            contextFactory,
            new TestHubContext(),
            NullLogger<ControllerStateStore>.Instance);
        await store.MarkAllOfflineAsync(CancellationToken.None);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            Assert.All(await db.JoinSnapshots.ToListAsync(), snapshot => Assert.False(snapshot.DigitalValue));
            var controller = await db.Controllers.SingleAsync(item => item.Id == controllerId);
            Assert.False(controller.IsOnline);
        }
    }

    private sealed class TestContextFactory(DbContextOptions<ControlDbContext> options)
        : IDbContextFactory<ControlDbContext>
    {
        public ControlDbContext CreateDbContext() => new(options);

        public Task<ControlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestHubContext : IHubContext<ControlHub>
    {
        public IHubClients Clients { get; } = new TestHubClients();
        public IGroupManager Groups { get; } = new TestGroupManager();
    }

    private sealed class TestHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new TestClientProxy();

        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
        public IClientProxy Others => Proxy;
        public IClientProxy OthersInGroup(string groupName) => Proxy;
    }

    private sealed class TestClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
