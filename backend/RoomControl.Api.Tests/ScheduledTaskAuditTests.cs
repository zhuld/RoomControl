using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using RoomControl.Api.Data;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证自动任务后台执行按每条下发指令写入操作记录，并保留逐教室执行记录。</summary>
public sealed class ScheduledControlJobTests
{
    private static readonly DateTimeOffset ScheduledAtUtc = new(2026, 11, 24, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteWritesExecutionAndCommandAuditRecord()
    {
        await using var harness = await Harness.CreateAsync(ScheduleKind.Weekly, ScheduledAtUtc, targetCount: 1);
        var job = CreateJob(harness);

        await job.Execute(new FakeJobExecutionContext(harness.Targets[0].ScheduleId, ScheduledAtUtc));

        Assert.Equal(new[] { harness.Targets[0].MappingId }, harness.Sender.MappingIds);

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var execution = Assert.Single(await db.ScheduleExecutions.ToListAsync());
        Assert.Equal("Succeeded", execution.Status);

        var log = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal(AuditLogCategories.Schedule, log.Category);
        Assert.Equal("自动执行自动任务 Join 7", log.Action);
        Assert.Equal("系统", log.Actor);
        Assert.Equal(StatusCodes.Status202Accepted, log.StatusCode);
        Assert.Equal("教室「A101」· 控制点「上课」· 自动任务「一键上课」", log.Target);
    }

    [Fact]
    public async Task GroupExecutionWritesOneAuditRecordPerTarget()
    {
        await using var harness = await Harness.CreateAsync(ScheduleKind.Weekly, ScheduledAtUtc, targetCount: 2);
        var job = CreateJob(harness);

        foreach (var target in harness.Targets)
        {
            await job.Execute(new FakeJobExecutionContext(target.ScheduleId, ScheduledAtUtc));
        }

        Assert.Equal(2, harness.Sender.MappingIds.Count);

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        Assert.Equal(2, await db.ScheduleExecutions.CountAsync());

        var logs = await db.AuditLogs.ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, log =>
        {
            Assert.Equal(AuditLogCategories.Schedule, log.Category);
            Assert.Equal("自动执行自动任务 Join 7", log.Action);
            Assert.Equal("系统", log.Actor);
            Assert.Equal(StatusCodes.Status202Accepted, log.StatusCode);
        });
        Assert.Equal(
            new[]
            {
                "教室「A101」· 控制点「上课」· 自动任务「一键上课」",
                "教室「A102」· 控制点「上课」· 自动任务「一键上课」"
            },
            logs.Select(log => log.Target).OrderBy(target => target, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task FailedExecuteStillWritesCommandAuditRecordForEveryTarget()
    {
        await using var harness = await Harness.CreateAsync(ScheduleKind.Weekly, ScheduledAtUtc, targetCount: 2);
        harness.Sender.Failing.Add(harness.Targets[0].MappingId);
        var job = CreateJob(harness);

        foreach (var target in harness.Targets)
        {
            await job.Execute(new FakeJobExecutionContext(target.ScheduleId, ScheduledAtUtc));
        }

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var failed = await db.ScheduleExecutions.SingleAsync(
            item => item.ScheduleId == harness.Targets[0].ScheduleId);
        Assert.Equal("Failed", failed.Status);
        Assert.Equal("中控离线", failed.Error);

        var logs = await db.AuditLogs.ToListAsync();
        Assert.Equal(2, logs.Count);
        var failedLog = logs.Single(log => log.Target.Contains("A101", StringComparison.Ordinal));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failedLog.StatusCode);
        var succeededLog = logs.Single(log => log.Target.Contains("A102", StringComparison.Ordinal));
        Assert.Equal(StatusCodes.Status202Accepted, succeededLog.StatusCode);
    }

    private static ScheduledControlJob CreateJob(Harness harness) =>
        new(
            harness.ContextFactory,
            harness.Sender,
            new AuditLogWriter(harness.ContextFactory, TimeProvider.System),
            NullLogger<ScheduledControlJob>.Instance);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Harness(
            SqliteConnection connection,
            IDbContextFactory<ControlDbContext> contextFactory,
            IReadOnlyList<(Guid ScheduleId, Guid MappingId)> targets)
        {
            _connection = connection;
            ContextFactory = contextFactory;
            Targets = targets;
        }

        public IDbContextFactory<ControlDbContext> ContextFactory { get; }

        public FakeSender Sender { get; } = new();

        public IReadOnlyList<(Guid ScheduleId, Guid MappingId)> Targets { get; }

        public static async Task<Harness> CreateAsync(
            ScheduleKind kind,
            DateTimeOffset runAtUtc,
            int targetCount)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ControlDbContext>()
                .UseSqlite(connection)
                .Options;
            var contextFactory = new TestContextFactory(options);
            var groupId = Guid.NewGuid();
            var targets = new List<(Guid, Guid)>();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                for (var index = 0; index < targetCount; index++)
                {
                    var room = new Room { Name = $"A10{index + 1}" };
                    var controller = new Controller
                    {
                        Room = room,
                        Name = $"{room.Name}-CIP",
                        Host = "localhost"
                    };
                    room.Controller = controller;
                    var mapping = new JoinMapping
                    {
                        Controller = controller,
                        Name = "上课",
                        Kind = JoinKind.Digital,
                        SendJoinNumber = 7,
                        StateJoinNumber = 2
                    };
                    controller.JoinMappings.Add(mapping);
                    db.Rooms.Add(room);
                    var schedule = new ControlSchedule
                    {
                        GroupId = groupId,
                        JoinMapping = mapping,
                        JoinMappingId = mapping.Id,
                        RoomName = room.Name,
                        JoinName = mapping.Name,
                        Name = "一键上课",
                        Kind = kind,
                        Enabled = true,
                        RunAtUtc = kind == ScheduleKind.Once ? runAtUtc : null,
                        WeekdayMask = kind == ScheduleKind.Weekly ? 127 : 0,
                        LocalTime = kind == ScheduleKind.Weekly ? new TimeSpan(8, 30, 0) : TimeSpan.Zero,
                        NextRunAtUtc = runAtUtc
                    };
                    db.Schedules.Add(schedule);
                    targets.Add((schedule.Id, mapping.Id));
                }

                await db.SaveChangesAsync();
            }

            return new Harness(connection, contextFactory, targets);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class FakeSender : IControlCommandSender
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

    private sealed class FakeJobExecutionContext(
        Guid scheduleId,
        DateTimeOffset scheduledFireTimeUtc) : IJobExecutionContext
    {
        private readonly JobDataMap _dataMap = CreateDataMap(scheduleId);

        public JobDataMap MergedJobDataMap => _dataMap;

        public DateTimeOffset? ScheduledFireTimeUtc => scheduledFireTimeUtc;

        public CancellationToken CancellationToken => CancellationToken.None;

        public IScheduler Scheduler => throw new NotSupportedException();

        public IJobDetail JobDetail => throw new NotSupportedException();

        public IJob JobInstance => throw new NotSupportedException();

        public ITrigger Trigger => throw new NotSupportedException();

        public ICalendar? Calendar => throw new NotSupportedException();

        public bool Recovering => throw new NotSupportedException();

        public TriggerKey RecoveringTriggerKey => throw new NotSupportedException();

        public int RefireCount => throw new NotSupportedException();

        public DateTimeOffset FireTimeUtc => throw new NotSupportedException();

        public DateTimeOffset? PreviousFireTimeUtc => throw new NotSupportedException();

        public DateTimeOffset? NextFireTimeUtc => throw new NotSupportedException();

        public string FireInstanceId => throw new NotSupportedException();

        public object? Result { get; set; }

        public TimeSpan JobRunTime => throw new NotSupportedException();

        public void Put(object key, object objectValue) => throw new NotSupportedException();

        public object? Get(object key) => throw new NotSupportedException();

        private static JobDataMap CreateDataMap(Guid scheduleId)
        {
            var map = new JobDataMap();
            map.Put("scheduleId", scheduleId.ToString());
            return map;
        }
    }
}

/// <summary>验证停机期间到期的一次性任务在恢复时按任务组写入一条操作记录。</summary>
[Collection(QuartzCollection.Name)]
public sealed class MissedOneTimeScheduleTests(QuartzSchedulerFixture quartz)
{
    [Fact]
    public async Task RestoreRecordsMissedGroupInSingleAuditLog()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();
        var contextFactory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();
        var groupId = Guid.NewGuid();

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var roomName in new[] { "A101", "A102" })
            {
                var room = new Room { Name = roomName };
                var controller = new Controller
                {
                    Room = room,
                    Name = $"{roomName}-CIP",
                    Host = "localhost"
                };
                room.Controller = controller;
                var mapping = new JoinMapping
                {
                    Controller = controller,
                    Name = "上课",
                    Kind = JoinKind.Digital,
                    SendJoinNumber = 1,
                    StateJoinNumber = 2
                };
                controller.JoinMappings.Add(mapping);
                db.Rooms.Add(room);
                db.Schedules.Add(new ControlSchedule
                {
                    GroupId = groupId,
                    JoinMapping = mapping,
                    JoinMappingId = mapping.Id,
                    RoomName = room.Name,
                    JoinName = mapping.Name,
                    Name = "一次性上课",
                    Kind = ScheduleKind.Once,
                    Enabled = true,
                    RunAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30)
                });
            }

            await db.SaveChangesAsync();
        }

        await quartz.CreateService(contextFactory).RestoreAsync(CancellationToken.None);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            Assert.Equal(2, await db.ScheduleExecutions.CountAsync());
            Assert.All(await db.ScheduleExecutions.ToListAsync(), execution =>
                Assert.Equal("Failed", execution.Status));

            var log = Assert.Single(await db.AuditLogs.ToListAsync());
            Assert.Equal(AuditLogCategories.Schedule, log.Category);
            Assert.Equal(ScheduleAudit.MissedAction, log.Action);
            Assert.Equal("系统", log.Actor);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, log.StatusCode);
            Assert.Equal("教室「A101」等 2 间 · 自动任务「一次性上课」", log.Target);
        }
    }
}

internal sealed class TestContextFactory(DbContextOptions<ControlDbContext> options)
    : IDbContextFactory<ControlDbContext>
{
    public ControlDbContext CreateDbContext() => new(options);

    public Task<ControlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
