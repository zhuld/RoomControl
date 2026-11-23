using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Contracts;
using RoomControl.Api.Data;
using RoomControl.Api.Hubs;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证 SignalR 房间分组操作会重新检查账号是否仍启用。</summary>
public sealed class ControlHubTests
{
    [Fact]
    public async Task JoinRoom_AddsGroupForActiveAccount()
    {
        await using var fixture = await ControlHubFixture.CreateAsync(isActive: true);

        await fixture.CreateHub().JoinRoom(Guid.NewGuid());

        Assert.False(fixture.Context.Aborted);
        Assert.Single(fixture.Groups.Added);
    }

    [Fact]
    public async Task JoinRoom_AbortsConnectionWhenAccountDisabled()
    {
        await using var fixture = await ControlHubFixture.CreateAsync(isActive: false);

        await fixture.CreateHub().JoinRoom(Guid.NewGuid());

        Assert.True(fixture.Context.Aborted);
        Assert.Empty(fixture.Groups.Added);
    }

    [Fact]
    public async Task LeaveRoom_AbortsConnectionWhenAccountDeleted()
    {
        await using var fixture = await ControlHubFixture.CreateAsync(isActive: true);
        var hub = fixture.CreateHub();
        fixture.Service.DeleteUser(fixture.UserId, "other-admin");

        await hub.LeaveRoom(Guid.NewGuid());

        Assert.True(fixture.Context.Aborted);
        Assert.Empty(fixture.Groups.Removed);
    }

    private sealed class ControlHubFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private ControlHubFixture(SqliteConnection connection, UserService service, Guid userId)
        {
            _connection = connection;
            Service = service;
            UserId = userId;
            Context = new TestCallerContext("ops");
            Groups = new TestGroupManager();
        }

        public UserService Service { get; }
        public Guid UserId { get; }
        public TestCallerContext Context { get; }
        public TestGroupManager Groups { get; }

        public static async Task<ControlHubFixture> CreateAsync(bool isActive)
        {
            // 共享内存 SQLite 连接使测试中的 UserService 和 Hub 使用同一份账号数据。
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ControlDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ControlDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var service = new UserService(db);
            service.CreateUser(new CreateUserRequest("other-admin", "secret1", UserRoleExtensions.AdminName, true));
            var user = service.CreateUser(
                new CreateUserRequest("ops", "secret1", UserRoleExtensions.OperatorName, isActive));
            return new ControlHubFixture(connection, service, user.Id);
        }

        public ControlHub CreateHub() => new(Service) { Context = Context, Groups = Groups };

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestCallerContext(string username) : HubCallerContext
    {
        private readonly ClaimsPrincipal _user =
            new(new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], "test"));

        public bool Aborted { get; private set; }
        public override string ConnectionId => "conn-1";
        public override string? UserIdentifier => username;
        public override ClaimsPrincipal? User => _user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override void Abort() => Aborted = true;
    }

    private sealed class TestGroupManager : IGroupManager
    {
        public List<string> Added { get; } = [];
        public List<string> Removed { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Added.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Removed.Add(groupName);
            return Task.CompletedTask;
        }
    }
}
