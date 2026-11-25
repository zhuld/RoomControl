using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RoomControl.Api.Data;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证手动任务的目标维护、批量下发结果与控制点删除后的清理。</summary>
public sealed class ManualTaskTests
{
    [Fact]
    public async Task CreatingTaskRequiresTheSameControlPointName()
    {
        await using var harness = await Harness.CreateAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.CreateAsync(
                [harness.MappingIds[0], harness.MappingIds[2]],
                "混合任务",
                true,
                CancellationToken.None));

        Assert.Contains("同名控制点", exception.Message);
    }

    [Fact]
    public async Task UpdatingTaskReplacesTargetsAndKeepsDefinition()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            [harness.MappingIds[0], harness.MappingIds[1]],
            "一键上课",
            true,
            CancellationToken.None);

        Assert.Equal(2, created.Targets.Count);
        Assert.Equal("上课", created.JoinName);
        Assert.True(created.ConfirmBeforeRun);

        var updated = await harness.Service.UpdateAsync(
            created.Id,
            [harness.MappingIds[1]],
            "一键上课（已调整）",
            false,
            CancellationToken.None);

        Assert.Single(updated.Targets);
        Assert.Equal("一键上课（已调整）", updated.Name);
        Assert.False(updated.ConfirmBeforeRun);

        await using (var db = await harness.ContextFactory.CreateDbContextAsync())
        {
            Assert.Equal(1, await db.ManualTaskTargets.CountAsync());
        }

        await harness.Service.DeleteAsync(created.Id, CancellationToken.None);

        await using (var db = await harness.ContextFactory.CreateDbContextAsync())
        {
            Assert.False(await db.ManualTasks.AnyAsync());
            Assert.False(await db.ManualTaskTargets.AnyAsync());
        }
    }

    [Fact]
    public async Task RunSendsPulseToEveryTargetAndStoresExecutionSummary()
    {
        await using var harness = await Harness.CreateAsync();
        var task = await harness.Service.CreateAsync(
            [harness.MappingIds[0], harness.MappingIds[1]],
            "一键上课",
            false,
            CancellationToken.None);

        var result = await harness.Service.RunAsync(task.Id, "operator1", CancellationToken.None);

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(2, harness.Sender.MappingIds.Count);
        Assert.All(harness.Sender.MappingIds, id => Assert.Contains(id, harness.MappingIds));

        var executions = await harness.Service.GetExecutionsAsync(task.Id, CancellationToken.None);
        var execution = Assert.Single(executions);
        Assert.Equal("Succeeded", execution.Status);
        Assert.Equal(2, execution.SucceededCount);
        Assert.Null(execution.Error);

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var logs = await db.AuditLogs.ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, log =>
        {
            Assert.Equal(AuditLogCategories.Control, log.Category);
            Assert.Equal("执行手动任务 Join 1", log.Action);
            Assert.Equal("operator1", log.Actor);
            Assert.Equal(202, log.StatusCode);
            Assert.Contains("手动任务「一键上课」", log.Target, StringComparison.Ordinal);
        });
        Assert.Equal(
            new[]
            {
                "教室「A101」· 控制点「上课」· 手动任务「一键上课」",
                "教室「A102」· 控制点「上课」· 手动任务「一键上课」"
            },
            logs.Select(log => log.Target).OrderBy(target => target, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task RunRecordsPartialFailureWithRoomAndReason()
    {
        await using var harness = await Harness.CreateAsync();
        var task = await harness.Service.CreateAsync(
            [harness.MappingIds[0], harness.MappingIds[1]],
            "一键上课",
            false,
            CancellationToken.None);
        harness.Sender.Failing.Add(harness.MappingIds[0]);

        var result = await harness.Service.RunAsync(task.Id, "operator1", CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains(result.Targets, target => target.Status == "Failed" && target.Error == "中控离线");

        var executions = await harness.Service.GetExecutionsAsync(task.Id, CancellationToken.None);
        var execution = Assert.Single(executions);
        Assert.Equal("Partial", execution.Status);
        Assert.Contains("中控离线", execution.Error);

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var logs = await db.AuditLogs.ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Equal(
            503,
            logs.Single(log => log.Target.Contains("A101", StringComparison.Ordinal)).StatusCode);
        Assert.Equal(
            202,
            logs.Single(log => log.Target.Contains("A102", StringComparison.Ordinal)).StatusCode);
    }

    [Fact]
    public async Task DeletingJoinMappingRemovesTargetAndDeletesEmptyTask()
    {
        await using var harness = await Harness.CreateAsync();
        var task = await harness.Service.CreateAsync(
            [harness.MappingIds[0]],
            "一键上课",
            true,
            CancellationToken.None);

        await harness.Service.DeleteForJoinMappingAsync(harness.MappingIds[0], CancellationToken.None);

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        Assert.False(await db.ManualTasks.AnyAsync(item => item.Id == task.Id));
        Assert.False(await db.ManualTaskTargets.AnyAsync());
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Harness(
            SqliteConnection connection,
            IDbContextFactory<ControlDbContext> contextFactory,
            FakePulseSender sender,
            Guid[] mappingIds)
        {
            _connection = connection;
            ContextFactory = contextFactory;
            Sender = sender;
            MappingIds = mappingIds;
            Service = new ManualTaskService(
                contextFactory,
                sender,
                new AuditLogWriter(contextFactory, TimeProvider.System),
                NullLogger<ManualTaskService>.Instance);
        }

        public IDbContextFactory<ControlDbContext> ContextFactory { get; }

        public FakePulseSender Sender { get; }

        public ManualTaskService Service { get; }

        public Guid[] MappingIds { get; }

        public static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ControlDbContext>()
                .UseSqlite(connection)
                .Options;
            var contextFactory = new TestContextFactory(options);
            var sender = new FakePulseSender();
            Guid[] mappingIds;

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                var definitions = new[]
                {
                    ("A101", "上课"),
                    ("A102", "上课"),
                    ("A103", "下课")
                };
                foreach (var (roomName, joinName) in definitions)
                {
                    var room = new Room { Name = roomName };
                    var controller = new Controller
                    {
                        Room = room,
                        Name = $"{roomName}-CIP",
                        Host = "localhost"
                    };
                    room.Controller = controller;
                    controller.JoinMappings.Add(new JoinMapping
                    {
                        Controller = controller,
                        Name = joinName,
                        Kind = JoinKind.Digital,
                        SendJoinNumber = 1,
                        StateJoinNumber = 2
                    });
                    db.Rooms.Add(room);
                }

                await db.SaveChangesAsync();
                mappingIds = await db.JoinMappings
                    .OrderBy(item => item.Controller.Room.Name)
                    .Select(item => item.Id)
                    .ToArrayAsync();
            }

            return new Harness(connection, contextFactory, sender, mappingIds);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakePulseSender : IControlCommandSender
    {
        public List<Guid> MappingIds { get; } = [];

        public HashSet<Guid> Failing { get; } = [];

        public Task SendDigitalPulseAsync(
            Guid mappingId,
            bool enforceEnableJoin,
            CancellationToken cancellationToken)
        {
            MappingIds.Add(mappingId);
            if (Failing.Contains(mappingId))
            {
                throw new ControllerUnavailableException("中控离线");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestContextFactory(DbContextOptions<ControlDbContext> options)
        : IDbContextFactory<ControlDbContext>
    {
        public ControlDbContext CreateDbContext() => new(options);

        public Task<ControlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
