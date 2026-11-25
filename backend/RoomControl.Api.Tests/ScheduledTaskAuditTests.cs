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

/// <summary>验证定时任务后台执行会同时写入执行记录与操作记录。</summary>
public sealed class ScheduledControlJobTests
{
    private const int SendJoinNumber = 7;
    private static readonly DateTimeOffset ScheduledAtUtc = new(2026, 11, 24, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteWritesExecutionAndAuditRecord()
    {
        await using var harness = await Harness.CreateAsync(ScheduleKind.Weekly, ScheduledAtUtc);
        var job = new ScheduledControlJob(
            harness.ContextFactory,
            harness.Sender,
            NullLogger<ScheduledControlJob>.Instance);

        await job.Execute(new FakeJobExecutionContext(harness.ScheduleId, ScheduledAtUtc));

        Assert.Equal(new[] { harness.MappingId }, harness.Sender.MappingIds);

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var execution = Assert.Single(await db.ScheduleExecutions.ToListAsync());
        Assert.Equal("Succeeded", execution.Status);

        var log = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal(AuditLogCategories.Schedule, log.Category);
        Assert.Equal("系统", log.Actor);
        Assert.Equal(StatusCodes.Status200OK, log.StatusCode);
        Assert.Contains($"Join {SendJoinNumber}", log.Action);
        Assert.Contains("一键上课", log.Target);
        Assert.Contains("A101", log.Target);
    }

    [Fact]
    public async Task FailedExecuteRecordsUnavailableControllerInAuditLog()
    {
        await using var harness = await Harness.CreateAsync(ScheduleKind.Weekly, ScheduledAtUtc);
        harness.Sender.Failing.Add(harness.MappingId);
        var job = new ScheduledControlJob(
            harness.ContextFactory,
            harness.Sender,
            NullLogger<ScheduledControlJob>.Instance);

        await job.Execute(new FakeJobExecutionContext(harness.ScheduleId, ScheduledAtUtc));

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var execution = Assert.Single(await db.ScheduleExecutions.ToListAsync());
        Assert.Equal("Failed", execution.Status);
        Assert.Equal("中控离线", execution.Error);

        var log = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal(AuditLogCategories.Schedule, log.Category);
        Assert.Equal("系统", log.Actor);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, log.StatusCode);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Harness(
            SqliteConnection connection,
            IDbContextFactory<ControlDbContext> contextFactory,
            Guid mappingId,
            Guid scheduleId)
        {
            _connection = connection;
            ContextFactory = contextFactory;
            MappingId = mappingId;
            ScheduleId = scheduleId;
        }

        public IDbContextFactory<ControlDbContext> ContextFactory { get; }

        public FakeSender Sender { get; } = new();

        public Guid MappingId { get; }

        public Guid ScheduleId { get; }

        public static async Task<Harness> CreateAsync(ScheduleKind kind, DateTimeOffset runAtUtc)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ControlDbContext>()
                .UseSqlite(connection)
                .Options;
            var contextFactory = new TestContextFactory(options);
            var mappingId = Guid.NewGuid();
            var scheduleId = Guid.NewGuid();

            await using (var db = await contextFactory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                var room = new Room { Name = "A101" };
                var controller = new Controller { Room = room, Name = "A101-CIP", Host = "localhost" };
                room.Controller = controller;
                var mapping = new JoinMapping
                {
                    Id = mappingId,
                    Controller = controller,
                    Name = "上课",
                    Kind = JoinKind.Digital,
                    SendJoinNumber = SendJoinNumber,
                    StateJoinNumber = 2
                };
                controller.JoinMappings.Add(mapping);
                db.Rooms.Add(room);
                db.Schedules.Add(new ControlSchedule
                {
                    Id = scheduleId,
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
                });
                await db.SaveChangesAsync();
            }

            return new Harness(connection, contextFactory, mappingId, scheduleId);
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

/// <summary>验证停机期间到期的一次性任务在恢复时写入失败执行记录与操作记录。</summary>
[Collection(QuartzCollection.Name)]
public sealed class MissedOneTimeScheduleTests(QuartzSchedulerFixture quartz)
{
    [Fact]
    public async Task RestoreRecordsMissedScheduleInAuditLog()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();
        var contextFactory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();

        Guid scheduleId;
        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var room = new Room { Name = "A101" };
            var controller = new Controller { Room = room, Name = "A101-CIP", Host = "localhost" };
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
            var schedule = new ControlSchedule
            {
                JoinMapping = mapping,
                JoinMappingId = mapping.Id,
                RoomName = room.Name,
                JoinName = mapping.Name,
                Name = "一次性上课",
                Kind = ScheduleKind.Once,
                Enabled = true,
                RunAtUtc = DateTimeOffset.UtcNow.AddMinutes(-30)
            };
            db.Rooms.Add(room);
            db.Schedules.Add(schedule);
            await db.SaveChangesAsync();
            scheduleId = schedule.Id;
        }

        await quartz.CreateService(contextFactory).RestoreAsync(CancellationToken.None);

        await using (var db = await contextFactory.CreateDbContextAsync())
        {
            var execution = await db.ScheduleExecutions.SingleAsync(item => item.ScheduleId == scheduleId);
            Assert.Equal("Failed", execution.Status);

            var log = await db.AuditLogs.SingleAsync();
            Assert.Equal(AuditLogCategories.Schedule, log.Category);
            Assert.Equal("系统", log.Actor);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, log.StatusCode);
            Assert.Contains("未自动执行", log.Action);
            Assert.Contains("一次性上课", log.Target);
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
