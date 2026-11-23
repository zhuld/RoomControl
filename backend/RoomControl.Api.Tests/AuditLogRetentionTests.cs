using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomControl.Api.Data;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证日志保留天数的默认值、校验与过期记录清理。</summary>
public sealed class AuditLogRetentionTests
{
    [Fact]
    public async Task PurgeExpiredRemovesLogsOlderThanDefaultRetention()
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await RetentionFixture.CreateAsync(now);

        await fixture.AddLogAsync(now.AddDays(-61), "expired");
        await fixture.AddLogAsync(now.AddDays(-60), "boundary");
        await fixture.AddLogAsync(now.AddDays(-1), "recent");

        var deleted = await fixture.Service.PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
        await using var db = await fixture.ContextFactory.CreateDbContextAsync();
        var actions = await db.AuditLogs.Select(log => log.Action).ToListAsync();
        Assert.DoesNotContain("expired", actions);
        Assert.Contains("boundary", actions);
        Assert.Contains("recent", actions);
    }

    [Fact]
    public async Task ZeroRetentionKeepsAllLogs()
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await RetentionFixture.CreateAsync(now);

        Assert.Equal(
            SiteSettings.DefaultLogRetentionDays,
            await fixture.Service.GetRetentionDaysAsync(CancellationToken.None));

        await fixture.AddLogAsync(now.AddDays(-365), "old");
        await fixture.Service.SetRetentionDaysAsync(0, CancellationToken.None);

        Assert.Equal(0, await fixture.Service.PurgeExpiredAsync(CancellationToken.None));
        await using var db = await fixture.ContextFactory.CreateDbContextAsync();
        Assert.True(await db.AuditLogs.AnyAsync(log => log.Action == "old"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(AuditLogRetentionService.MaxRetentionDays + 1)]
    public async Task RejectsRetentionDaysOutOfRange(int days)
    {
        await using var fixture = await RetentionFixture.CreateAsync(DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Service.SetRetentionDaysAsync(days, CancellationToken.None));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RetentionFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ServiceProvider provider;

        private RetentionFixture(SqliteConnection connection, ServiceProvider provider)
        {
            this.connection = connection;
            this.provider = provider;
        }

        public IDbContextFactory<ControlDbContext> ContextFactory =>
            provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();

        public AuditLogRetentionService Service =>
            provider.GetRequiredService<AuditLogRetentionService>();

        public static async Task<RetentionFixture> CreateAsync(DateTimeOffset now)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            services.AddSingleton<AuditLogRetentionService>();
            var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            return new RetentionFixture(connection, provider);
        }

        public async Task AddLogAsync(DateTimeOffset occurredAtUtc, string action)
        {
            await using var db = await ContextFactory.CreateDbContextAsync();
            db.AuditLogs.Add(new AuditLog
            {
                Category = AuditLogCategories.Configuration,
                Action = action,
                Actor = "tester",
                OccurredAtUtc = occurredAtUtc
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
