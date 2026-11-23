using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>写入审计记录；HTTP 请求与设备状态变化共用同一入口。</summary>
public sealed class AuditLogWriter(
    IDbContextFactory<ControlDbContext> dbContextFactory,
    TimeProvider timeProvider)
{
    /// <summary>写入一条审计记录；使用独立上下文且不随请求取消，避免响应已发出时丢失日志。</summary>
    public async Task WriteAsync(
        string category,
        string action,
        string target,
        string actor,
        int statusCode)
    {
        // 日志写入是旁路操作，使用独立 DbContext，避免影响业务请求的取消或事务。
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        db.AuditLogs.Add(new AuditLog
        {
            Category = category,
            Action = action,
            Target = target,
            Actor = actor,
            StatusCode = statusCode,
            OccurredAtUtc = timeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
