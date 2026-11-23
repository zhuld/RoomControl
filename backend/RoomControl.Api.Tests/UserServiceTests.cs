using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Contracts;
using RoomControl.Api.Data;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证用户输入、管理员保留规则、密码精确性及登录账号列表行为。</summary>
public sealed class UserServiceTests
{
    [Fact]
    public async Task CreateUser_RejectsUnknownRoleAndBlankUsername()
    {
        await using var fixture = await UserServiceFixture.CreateAsync();

        Assert.Throws<ArgumentException>(
            () => fixture.Service.CreateUser(new CreateUserRequest("ops", "secret1", "owner", true)));
        Assert.Throws<ArgumentException>(
            () => fixture.Service.CreateUser(new CreateUserRequest("   ", "secret1", "viewer", true)));
    }

    [Fact]
    public async Task UpdateUser_PreventsDisablingTheLastActiveAdmin()
    {
        await using var fixture = await UserServiceFixture.CreateAsync();
        var admin = fixture.Service.CreateUser(new CreateUserRequest("admin", "secret1", UserRoleExtensions.AdminName, true));
        var other = fixture.Service.CreateUser(new CreateUserRequest("ops", "secret1", UserRoleExtensions.OperatorName, true));

        var error = Assert.Throws<InvalidOperationException>(
            () => fixture.Service.UpdateUser(admin.Id, new UpdateUserRequest(null, null, false), other.Username));
        Assert.Contains("管理员", error.Message);

        fixture.Service.CreateUser(new CreateUserRequest("admin2", "secret1", UserRoleExtensions.AdminName, true));
        var updated = fixture.Service.UpdateUser(admin.Id, new UpdateUserRequest(null, null, false), other.Username);
        Assert.NotNull(updated);
        Assert.False(updated.IsActive);
    }

    [Fact]
    public async Task UpdateUser_PreservesPasswordCharactersExactly()
    {
        await using var fixture = await UserServiceFixture.CreateAsync();
        var user = fixture.Service.CreateUser(new CreateUserRequest("ops", "secret1", UserRoleExtensions.OperatorName, true));

        fixture.Service.UpdateUser(user.Id, new UpdateUserRequest(" spaced pw ", null, null), "admin");

        Assert.NotNull(fixture.Service.Authenticate("ops", " spaced pw "));
        Assert.Null(fixture.Service.Authenticate("ops", "spaced pw"));
        Assert.Null(fixture.Service.Authenticate("ops", "secret1"));
    }

    [Fact]
    public async Task UpdateUser_AllowsChangingOwnPassword()
    {
        await using var fixture = await UserServiceFixture.CreateAsync();
        var admin = fixture.Service.CreateUser(new CreateUserRequest("admin", "secret1", UserRoleExtensions.AdminName, true));

        var updated = fixture.Service.UpdateUser(admin.Id, new UpdateUserRequest("new-secret", null, null), "admin");

        Assert.NotNull(updated);
        Assert.NotNull(fixture.Service.Authenticate("admin", "new-secret"));
        Assert.Null(fixture.Service.Authenticate("admin", "secret1"));
    }

    [Fact]
    public async Task GetActiveRole_ReturnsNullForDisabledUser()
    {
        await using var fixture = await UserServiceFixture.CreateAsync();
        var user = fixture.Service.CreateUser(new CreateUserRequest("ops", "secret1", UserRoleExtensions.OperatorName, true));
        Assert.Equal(UserRole.Operator, fixture.Service.GetActiveRole("ops"));

        fixture.Service.UpdateUser(user.Id, new UpdateUserRequest(null, null, false), "admin");

        Assert.Null(fixture.Service.GetActiveRole("ops"));
    }

    [Fact]
    public async Task GetLoginAccounts_ReturnsOnlyActiveAccountsOrderedByUsername()
    {
        await using var fixture = await UserServiceFixture.CreateAsync();
        fixture.Service.CreateUser(new CreateUserRequest("zoe", "secret1", UserRoleExtensions.ViewerName, true));
        var disabled = fixture.Service.CreateUser(new CreateUserRequest("bob", "secret1", UserRoleExtensions.OperatorName, false));
        fixture.Service.CreateUser(new CreateUserRequest("amy", "secret1", UserRoleExtensions.AdminName, true));

        var accounts = fixture.Service.GetLoginAccounts().ToList();

        Assert.Equal(["amy", "zoe"], accounts.Select(account => account.Username));
        Assert.DoesNotContain(accounts, account => account.Username == disabled.Username);
        Assert.Equal(UserRoleExtensions.AdminName, accounts[0].Role);
    }

    private sealed class UserServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private UserServiceFixture(SqliteConnection connection, UserService service)
        {
            _connection = connection;
            Service = service;
        }

        public UserService Service { get; }

        public static async Task<UserServiceFixture> CreateAsync()
        {
            // 固定打开内存连接，确保 EnsureCreated 创建的数据库在整个用例期间存活。
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ControlDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ControlDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new UserServiceFixture(connection, new UserService(db));
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
