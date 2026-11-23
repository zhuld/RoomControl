using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>读取、保存日志保留天数，并按保留策略清理过期记录。</summary>
public sealed class AuditLogRetentionService(
    IDbContextFactory<ControlDbContext> dbContextFactory,
    TimeProvider timeProvider)
{
    /// <summary>允许配置的最大保留天数（约 10 年）。</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>未保存过设置时返回默认值；0 表示永久保留。</summary>
    public async Task<int> GetRetentionDaysAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.SiteSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == SiteSettings.SingletonId, cancellationToken);
        return settings?.LogRetentionDays ?? SiteSettings.DefaultLogRetentionDays;
    }

    /// <summary>保存保留天数；0 表示永久保留。</summary>
    public async Task SetRetentionDaysAsync(int retentionDays, CancellationToken cancellationToken)
    {
        if (retentionDays is < 0 or > MaxRetentionDays)
        {
            throw new ArgumentException($"日志保留天数需在 0–{MaxRetentionDays} 天之间，0 表示永久保留。");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.SiteSettings
            .SingleOrDefaultAsync(item => item.Id == SiteSettings.SingletonId, cancellationToken);
        if (settings is null)
        {
            settings = new SiteSettings { Id = SiteSettings.SingletonId };
            db.SiteSettings.Add(settings);
        }

        settings.LogRetentionDays = retentionDays;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>删除早于保留天数的记录并返回删除条数；永久保留时不做清理。</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var retentionDays = await GetRetentionDaysAsync(cancellationToken);
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = timeProvider.GetUtcNow().AddDays(-retentionDays);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AuditLogs
            .Where(log => log.OccurredAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
