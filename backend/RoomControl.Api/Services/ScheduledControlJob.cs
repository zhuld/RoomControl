using Microsoft.EntityFrameworkCore;
using Quartz;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>Quartz 执行入口；记录执行结果与每条下发指令的操作记录，并推进重复任务的下次执行时间。</summary>
[DisallowConcurrentExecution]
public sealed class ScheduledControlJob(
    IDbContextFactory<ControlDbContext> contextFactory,
    IControlCommandSender connectionService,
    AuditLogWriter auditLogWriter,
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

        var mapping = schedule.JoinMapping;
        try
        {
            // 自动任务属于自动执行，不因使能 Join 为低而被拒绝。
            await connectionService.SendDigitalPulseAsync(
                mapping.Id,
                enforceEnableJoin: false,
                context.CancellationToken);
            execution.Status = "Succeeded";
            // 自动执行不经过 HTTP 审计中间件；每条下发指令单独留痕，便于按教室追溯。
            await TryWriteCommandAuditAsync(schedule, mapping.SendJoinNumber, AuditLogResults.CommandAccepted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            execution.Status = "Failed";
            execution.Error = exception.Message;
            logger.LogWarning(exception, "Scheduled control {ScheduleId} failed.", schedule.Id);
            await TryWriteCommandAuditAsync(
                schedule,
                mapping.SendJoinNumber,
                AuditLogResults.FromException(exception));
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

            // 一次性任务执行后自动停用；执行记录与下次执行时间一并落库。
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>操作记录是旁路留痕：写入失败只记服务端日志，不影响任务执行与状态推进。</summary>
    private async Task TryWriteCommandAuditAsync(ControlSchedule schedule, int sendJoinNumber, int statusCode)
    {
        try
        {
            await auditLogWriter.WriteAsync(
                AuditLogCategories.Schedule,
                ScheduleAudit.FormatCommandAction(sendJoinNumber),
                ScheduleAudit.FormatCommandTarget(schedule),
                "系统",
                statusCode);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to write command audit log for scheduled control {ScheduleId}.",
                schedule.Id);
        }
    }

}
