using Microsoft.EntityFrameworkCore;
using Quartz;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>Quartz 执行入口；记录每次结果并推进重复任务的下次执行时间。</summary>
[DisallowConcurrentExecution]
public sealed class ScheduledControlJob(
    IDbContextFactory<ControlDbContext> contextFactory,
    IControlCommandSender connectionService,
    ILogger<ScheduledControlJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        // 从 Job 数据中读取稳定 ID，再以数据库当前状态为准决定是否执行。
        if (!Guid.TryParse(context.MergedJobDataMap.GetString("scheduleId"), out var scheduleId))
        {
            throw new JobExecutionException("Quartz job has an invalid schedule ID.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(context.CancellationToken);
        var schedule = await db.Schedules.Include(item => item.JoinMapping)
            .SingleOrDefaultAsync(item => item.Id == scheduleId, context.CancellationToken);
        if (schedule is null || !schedule.Enabled || schedule.JoinMapping is null)
        {
            return;
        }

        var scheduledAtUtc = context.ScheduledFireTimeUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var execution = new ScheduleExecution
        {
            ScheduleId = schedule.Id,
            ScheduledAtUtc = scheduledAtUtc,
            Status = "Pending"
        };
        db.ScheduleExecutions.Add(execution);
        var auditStatusCode = 0;

        try
        {
            // 定时任务属于自动执行，不因使能 Join 为低而被拒绝。
            await connectionService.SendDigitalPulseAsync(
                schedule.JoinMapping.Id,
                enforceEnableJoin: false,
                context.CancellationToken);
            execution.Status = "Succeeded";
            auditStatusCode = StatusCodes.Status200OK;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            execution.Status = "Failed";
            execution.Error = exception.Message;
            auditStatusCode = exception is ControllerUnavailableException
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status500InternalServerError;
            logger.LogWarning(exception, "Scheduled control {ScheduleId} failed.", schedule.Id);
        }
        finally
        {
            // 无论指令成功或失败都落执行记录；重复任务根据完成时刻重新计算下次时间。
            execution.CompletedAtUtc = DateTimeOffset.UtcNow;
            if (schedule.Kind == ScheduleKind.Once)
            {
                schedule.Enabled = false;
                schedule.NextRunAtUtc = null;
            }
            else
            {
                schedule.NextRunAtUtc = WeeklyScheduleCalculator.GetNextOccurrence(
                    execution.CompletedAtUtc.Value,
                    schedule.WeekdayMask,
                    schedule.LocalTime);
            }

            // 自动执行不经过 HTTP 审计中间件，这里补写操作记录，让发送结果可在「操作记录」中追溯。
            db.AuditLogs.Add(new AuditLog
            {
                Category = AuditLogCategories.Schedule,
                Action = $"自动执行定时任务 Join {schedule.JoinMapping.SendJoinNumber}",
                Target = $"教室「{schedule.RoomName}」· 定时任务「{schedule.Name}」",
                Actor = "系统",
                StatusCode = auditStatusCode,
                OccurredAtUtc = execution.CompletedAtUtc.Value,
            });

            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

}
