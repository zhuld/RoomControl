using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RoomControl.Api.Data;
using RoomControl.Api.Hubs;
using RoomControl.Api.Integrations;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证使能 Join 只限制手动操作，自动任务不受其影响。</summary>
public sealed class ControllerConnectionServiceTests
{
    [Fact]
    public async Task Pulse_RejectedForManualOperationWhenEnableJoinIsLow()
    {
        await using var harness = await Harness.CreateAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.SendDigitalPulseAsync(harness.MappingId, enforceEnableJoin: true, CancellationToken.None));

        Assert.Contains("使能 Join", exception.Message);
        Assert.Empty(harness.Adapter.Pulses);
    }

    [Fact]
    public async Task Pulse_AllowedForAutomationWhenEnableJoinIsLow()
    {
        await using var harness = await Harness.CreateAsync();

        await harness.Service.SendDigitalPulseAsync(harness.MappingId, enforceEnableJoin: false, CancellationToken.None);

        Assert.Equal(new[] { 5 }, harness.Adapter.Pulses);
        Assert.Equal(1, harness.Adapter.StateRequestCount);
    }

    [Fact]
    public async Task Pulse_AllowedForManualOperationWhenEnableJoinIsHigh()
    {
        await using var harness = await Harness.CreateAsync(enableHigh: true);

        await harness.Service.SendDigitalPulseAsync(harness.MappingId, enforceEnableJoin: true, CancellationToken.None);

        Assert.Equal(new[] { 5 }, harness.Adapter.Pulses);
    }

    [Fact]
    public async Task Connect_StartsOnlyAfterApplicationStarted()
    {
        var lifetime = new TestApplicationLifetime();
        await using var harness = await Harness.CreateAsync(lifetime: lifetime);

        // 宿主启动完成前不建立中控连接。
        var early = await Task.WhenAny(harness.Adapter.Running, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(harness.Adapter.Running, early);

        lifetime.MarkStarted();

        await harness.Adapter.Running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Harness(
            SqliteConnection connection,
            ControllerConnectionService service,
            TestAdapters.Adapter adapter,
            Guid mappingId,
            TestApplicationLifetime lifetime)
        {
            _connection = connection;
            Service = service;
            Adapter = adapter;
            MappingId = mappingId;
            Lifetime = lifetime;
        }

        public ControllerConnectionService Service { get; }

        public TestAdapters.Adapter Adapter { get; }

        public Guid MappingId { get; }

        public TestApplicationLifetime Lifetime { get; }

        public static async Task<Harness> CreateAsync(
            bool enableHigh = false,
            TestApplicationLifetime? lifetime = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ControlDbContext>()
                .UseSqlite(connection)
                .Options;
            var contextFactory = new TestContextFactory(options);
            var adapter = new TestAdapters.Adapter();
            Guid mappingId;
            Guid controllerId;

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
                    SendJoinNumber = 5,
                    StateJoinNumber = 6,
                    EnableJoinNumber = 7
                };
                controller.JoinMappings.Add(mapping);
                db.Rooms.Add(room);

                await db.SaveChangesAsync();
                mappingId = mapping.Id;
                controllerId = controller.Id;
            }

            var stateStore = new ControllerStateStore(
                contextFactory,
                new TestHubContext(),
                NullLogger<ControllerStateStore>.Instance);
            lifetime ??= TestApplicationLifetime.Started();
            var service = new ControllerConnectionService(
                contextFactory,
                new TestAdapters.Factory(adapter),
                stateStore,
                lifetime,
                NullLogger<ControllerConnectionService>.Instance);

            await service.StartAsync(CancellationToken.None);
            var harness = new Harness(connection, service, adapter, mappingId, lifetime);
            if (!lifetime.IsStarted)
            {
                // 未启动的宿主下不等待连接，由用例自行验证"启动前不连接"。
                return harness;
            }

            try
            {
                // 适配器进入运行循环时连接已注册，此时再发起脉冲请求才代表真实的在线场景。
                await adapter.Running.WaitAsync(TimeSpan.FromSeconds(10));
                if (enableHigh)
                {
                    // 连接建立后使能快照会重置为低电平，只有设备上报才是高电平来源。
                    await using var db = await contextFactory.CreateDbContextAsync();
                    db.JoinSnapshots.Add(new JoinSnapshot
                    {
                        ControllerId = controllerId,
                        Kind = JoinKind.Digital,
                        JoinNumber = 7,
                        DigitalValue = true,
                        LastReportedAtUtc = DateTimeOffset.UtcNow
                    });
                    await db.SaveChangesAsync();
                }
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }

            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>可控的宿主生命周期，用于验证启动完成前不会连接中控。</summary>
    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();

        public bool IsStarted => _started.IsCancellationRequested;

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void MarkStarted() => _started.Cancel();

        public void StopApplication()
        {
        }

        public static TestApplicationLifetime Started()
        {
            var lifetime = new TestApplicationLifetime();
            lifetime.MarkStarted();
            return lifetime;
        }
    }

    private static class TestAdapters
    {
        public sealed class Factory(Adapter adapter) : IControllerAdapterFactory
        {
            public IControllerAdapter Create(Controller controller) => adapter;
        }

        public sealed class Adapter : IControllerAdapter
        {
            private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _stateRequestCount;

            public event Func<AdapterJoinUpdate, Task>? JoinUpdated
            {
                add { }
                remove { }
            }

            public event Func<IReadOnlyCollection<int>, Task>? StateRefreshCompleted
            {
                add { }
                remove { }
            }

            public Task Running => _running.Task;

            public ConcurrentQueue<int> Pulses { get; } = new();

            public int StateRequestCount => Volatile.Read(ref _stateRequestCount);

            public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task RunAsync(CancellationToken cancellationToken)
            {
                _running.TrySetResult();
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }

            public Task SendDigitalPulseAsync(int joinNumber, CancellationToken cancellationToken)
            {
                Pulses.Enqueue(joinNumber);
                return Task.CompletedTask;
            }

            public Task RequestStateAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _stateRequestCount);
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
