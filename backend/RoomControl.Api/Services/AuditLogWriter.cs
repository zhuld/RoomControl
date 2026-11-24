using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>写入审计记录；HTTP 请求与设备状态变化共用同一入口。</summary>
public sealed class AuditLogWriter(
    IDbContextFactory<ControlDbContext> dbContextFactory,
    TimeProvider timeProvider)
{
    // 与 ControlDbContext 中的列长度保持一致；超长文本截断，避免 PostgreSQL 拒绝整条审计记录。
    private const int MaxActionLength = 300;
    private const int MaxTargetLength = 512;
    private const int MaxActorLength = 60;

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
            Action = Truncate(action, MaxActionLength),
            Target = Truncate(target, MaxTargetLength),
            Actor = Truncate(actor, MaxActorLength),
            StatusCode = statusCode,
            OccurredAtUtc = timeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = maxLength - 1;
        // 不要拆分代理项对，避免截断后产生无效的 UTF-16 文本。
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }
        return value[..length] + "…";
    }
}
