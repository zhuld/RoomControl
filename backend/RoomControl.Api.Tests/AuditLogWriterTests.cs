using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomControl.Api.Data;
using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证审计写入会截断超长字段，避免数据库列长度导致整条日志丢失。</summary>
public sealed class AuditLogWriterTests
{
    [Fact]
    public async Task WriteAsync_TruncatesFieldsToColumnLimits()
    {
        await using var fixture = await WriterFixture.CreateAsync();

        await fixture.Writer.WriteAsync(
            AuditLogCategories.Configuration,
            new string('a', 400),
            new string('b', 600),
            new string('c', 80),
            statusCode: 200);

        await using var db = await fixture.ContextFactory.CreateDbContextAsync();
        var log = await db.AuditLogs.SingleAsync();
        Assert.Equal(300, log.Action.Length);
        Assert.EndsWith("…", log.Action);
        Assert.Equal(512, log.Target.Length);
        Assert.EndsWith("…", log.Target);
        Assert.Equal(60, log.Actor.Length);
        Assert.Equal(200, log.StatusCode);
    }

    [Fact]
    public async Task WriteAsync_KeepsValuesWithinLimits()
    {
        await using var fixture = await WriterFixture.CreateAsync();

        await fixture.Writer.WriteAsync(
            AuditLogCategories.Configuration,
            "创建教室",
            "教室「A楼 · 1F · R101」",
            "admin",
            statusCode: 201);

        await using var db = await fixture.ContextFactory.CreateDbContextAsync();
        var log = await db.AuditLogs.SingleAsync();
        Assert.Equal("创建教室", log.Action);
        Assert.Equal("教室「A楼 · 1F · R101」", log.Target);
        Assert.Equal("admin", log.Actor);
    }

    private sealed class WriterFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ServiceProvider provider;

        private WriterFixture(SqliteConnection connection, ServiceProvider provider)
        {
            this.connection = connection;
            this.provider = provider;
        }

        public IDbContextFactory<ControlDbContext> ContextFactory =>
            provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();

        public AuditLogWriter Writer => provider.GetRequiredService<AuditLogWriter>();

        public static async Task<WriterFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddDbContextFactory<ControlDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<AuditLogWriter>();
            var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IDbContextFactory<ControlDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            return new WriterFixture(connection, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
