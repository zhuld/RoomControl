using Quartz;

namespace RoomControl.Api.Services;

/// <summary>Quartz 每日任务：按站点设置清理过期的操作日志。</summary>
[DisallowConcurrentExecution]
public sealed class AuditLogCleanupJob(
    AuditLogRetentionService retentionService,
    ILogger<AuditLogCleanupJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var deleted = await retentionService.PurgeExpiredAsync(context.CancellationToken);
            if (deleted > 0)
            {
                logger.LogInformation("Purged {Count} expired audit log entries.", deleted);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 清理失败不阻塞后续调度；下次执行仍会按当前保留天数重试。
            logger.LogWarning(exception, "Failed to purge expired audit log entries.");
        }
    }
}
