using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;
using RoomControl.Api.Hubs;
using RoomControl.Api.Integrations;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>持久化中控连接和 Join 状态，并向对应房间的 SignalR 客户端推送变化。</summary>
public sealed class ControllerStateStore(
    IDbContextFactory<ControlDbContext> contextFactory,
    IHubContext<ControlHub> hubContext,
    ILogger<ControllerStateStore> logger)
{
    /// <summary>
    /// 更新中控在线状态与最近错误；重连成功时先重置使能快照，
    /// 再在同一事务中写入连接审计并向该教室推送 <c>controllerStatusChanged</c>。
    /// </summary>
    public async Task SetConnectionAsync(Guid controllerId, bool isOnline, string? error, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var controller = await db.Controllers
            .Include(item => item.Room)
            .SingleOrDefaultAsync(item => item.Id == controllerId, cancellationToken);
        if (controller is null)
        {
            return;
        }

        var wasOnline = controller.IsOnline;
        controller.IsOnline = isOnline;
        controller.LastError = error;
        controller.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (isOnline)
        {
            // 重连后使能状态必须重新由设备上报，不能沿用断线前的缓存值。
            await ResetEnableSnapshotsAsync(db, controllerId, cancellationToken);
            if (!wasOnline)
            {
                AddConnectionAuditLog(db, controller, isOnline: true);
            }
        }
        else if (wasOnline)
        {
            AddConnectionAuditLog(db, controller, isOnline: false);
        }

        await db.SaveChangesAsync(cancellationToken);
        await hubContext.Clients.Group($"room:{controller.RoomId}")
            .SendAsync("controllerStatusChanged", new
            {
                controllerId,
                controller.IsOnline,
                controller.LastError,
                controller.UpdatedAtUtc
            }, cancellationToken);
    }

    /// <summary>
    /// 保存设备上报的 Join 快照；仅在电平真正变化时写入状态审计，
    /// 并向该教室推送 <c>joinStateChanged</c>。
    /// </summary>
    public async Task SaveJoinUpdateAsync(Guid controllerId, AdapterJoinUpdate update, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var controller = await db.Controllers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == controllerId, cancellationToken);
        if (controller is null)
        {
            return;
        }

        // 以控制器、信号类型、Join 编号组成的复合键更新最新快照。
        var snapshot = await db.JoinSnapshots.FindAsync(
            [controllerId, JoinKind.Digital, update.JoinNumber], cancellationToken);
        // 从未上报过的 Join 按低电平处理，首次低电平回报不算状态变化。
        var previousValue = snapshot?.DigitalValue ?? false;
        if (snapshot is null)
        {
            snapshot = new JoinSnapshot
            {
                ControllerId = controllerId,
                Kind = JoinKind.Digital,
                JoinNumber = update.JoinNumber
            };
            db.JoinSnapshots.Add(snapshot);
        }

        snapshot.DigitalValue = update.Value;
        snapshot.LastReportedAtUtc = DateTimeOffset.UtcNow;

        var mappings = await db.JoinMappings.AsNoTracking()
            .Where(join =>
                join.ControllerId == controllerId &&
                join.Kind == JoinKind.Digital &&
                (join.StateJoinNumber == update.JoinNumber || join.EnableJoinNumber == update.JoinNumber))
            .Select(join => new { join.Id, join.Name })
            .ToArrayAsync(cancellationToken);

        // 只有电平真正变化时才记录，避免设备周期回报刷满日志。
        if (previousValue != update.Value)
        {
            var room = await db.Rooms.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == controller.RoomId, cancellationToken);
            db.AuditLogs.Add(new AuditLog
            {
                Category = AuditLogCategories.State,
                Action = $"状态变更 Join {update.JoinNumber}：{AuditLogFormatting.FormatLevel(previousValue)} → {AuditLogFormatting.FormatLevel(update.Value)}",
                Target = AuditLogFormatting.FormatJoinTarget(
                    room,
                    mappings.Select(join => join.Name).Distinct().ToArray(),
                    update.JoinNumber),
                Actor = "设备",
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        var mappingIds = mappings.Select(join => join.Id).ToArray();
        await hubContext.Clients.Group($"room:{controller.RoomId}")
            .SendAsync("joinStateChanged", new
            {
                controllerId,
                mappingIds,
                update.JoinNumber,
                update.Value,
                snapshot.LastReportedAtUtc
            }, cancellationToken);
    }

    /// <summary>
    /// 一次状态回放结束时调用：中控只在回放中上报高电平的 Join，
    /// 未出现在本次回放里的状态 Join 视为低电平，避免界面停留在过期的高电平。
    /// </summary>
    public async Task ReconcileStateSnapshotsAsync(
        Guid controllerId,
        IReadOnlyCollection<int> reportedJoinNumbers,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var controller = await db.Controllers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == controllerId, cancellationToken);
        if (controller is null)
        {
            return;
        }

        var mappings = await db.JoinMappings.AsNoTracking()
            .Where(join => join.ControllerId == controllerId && join.Kind == JoinKind.Digital)
            .Select(join => new { join.Id, join.Name, join.StateJoinNumber, join.EnableJoinNumber })
            .ToArrayAsync(cancellationToken);
        var stateJoinNumbers = mappings
            .Select(join => join.StateJoinNumber)
            .Where(join => join > 0)
            .Distinct()
            .ToArray();
        if (stateJoinNumbers.Length == 0)
        {
            return;
        }

        // 回放刚结束，本次上报的 Join 已按序写入；仍为高电平且本次没有上报的才需要纠正为低电平。
        // 先按状态 Join 编号取回快照，再在内存中比较上报集合，避免把设备上报的全部编号展开成 SQL IN 参数。
        var reportedJoins = reportedJoinNumbers.ToHashSet();
        var staleSnapshots = (await db.JoinSnapshots
                .Where(snapshot =>
                    snapshot.ControllerId == controllerId &&
                    snapshot.Kind == JoinKind.Digital &&
                    stateJoinNumbers.Contains(snapshot.JoinNumber) &&
                    snapshot.DigitalValue)
                .ToListAsync(cancellationToken))
            .Where(snapshot => !reportedJoins.Contains(snapshot.JoinNumber))
            .ToList();
        if (staleSnapshots.Count == 0)
        {
            return;
        }

        foreach (var snapshot in staleSnapshots)
        {
            snapshot.DigitalValue = false;
        }

        // 回放结束后的纠偏同样是状态变化，需要留痕。
        var room = await db.Rooms.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == controller.RoomId, cancellationToken);
        foreach (var snapshot in staleSnapshots)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Category = AuditLogCategories.State,
                Action = $"状态变更 Join {snapshot.JoinNumber}：高电平 → 低电平",
                Target = AuditLogFormatting.FormatJoinTarget(
                    room,
                    mappings
                        .Where(join => join.StateJoinNumber == snapshot.JoinNumber || join.EnableJoinNumber == snapshot.JoinNumber)
                        .Select(join => join.Name)
                        .Distinct()
                        .ToArray(),
                    snapshot.JoinNumber),
                Actor = "设备",
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var snapshot in staleSnapshots)
        {
            var mappingIds = mappings
                .Where(join => join.StateJoinNumber == snapshot.JoinNumber || join.EnableJoinNumber == snapshot.JoinNumber)
                .Select(join => join.Id)
                .ToArray();
            await hubContext.Clients.Group($"room:{controller.RoomId}")
                .SendAsync("joinStateChanged", new
                {
                    controllerId,
                    mappingIds,
                    snapshot.JoinNumber,
                    snapshot.DigitalValue,
                    snapshot.LastReportedAtUtc
                }, cancellationToken);
        }
    }

    /// <summary>进程启动时清除残留的在线标记，并把所有数字快照重置为低电平。</summary>
    public async Task MarkAllOfflineAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // 上次进程退出时无法可靠写离线状态，启动时将数据库残留的在线标记统一清除。
        var onlineControllers = await db.Controllers
            .Include(item => item.Room)
            .Where(item => item.IsOnline)
            .ToListAsync(cancellationToken);
        foreach (var controller in onlineControllers)
        {
            controller.IsOnline = false;
            controller.LastError = "API 正在启动，等待重新连接。";
            controller.UpdatedAtUtc = DateTimeOffset.UtcNow;
            AddConnectionAuditLog(db, controller, isOnline: false);
        }

        // 启动后尚未收到设备回报，所有状态按低电平处理，避免沿用上次进程留下的高电平。
        var highSnapshots = await db.JoinSnapshots
            .Where(snapshot => snapshot.Kind == JoinKind.Digital && snapshot.DigitalValue)
            .ToListAsync(cancellationToken);
        foreach (var snapshot in highSnapshots)
        {
            snapshot.DigitalValue = false;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void AddConnectionAuditLog(ControlDbContext db, Controller controller, bool isOnline) =>
        db.AuditLogs.Add(new AuditLog
        {
            Category = AuditLogCategories.Connection,
            Action = isOnline ? "中控连接成功" : "中控连接断开",
            Target = $"{AuditLogFormatting.FormatRoom(controller.Room)}· 中控「{controller.Name}」",
            Actor = "系统",
        });

    public void LogUpdateFailure(Guid controllerId, Exception exception) =>
        logger.LogError(exception, "Failed to persist CIP state update for controller {ControllerId}", controllerId);

    private static async Task ResetEnableSnapshotsAsync(
        ControlDbContext db,
        Guid controllerId,
        CancellationToken cancellationToken)
    {
        // 仅重置使能 Join；普通状态 Join 保留最后状态，等待状态回放刷新。
        var enableJoinNumbers = await db.JoinMappings.AsNoTracking()
            .Where(join =>
                join.ControllerId == controllerId &&
                join.Kind == JoinKind.Digital &&
                join.EnableJoinNumber > 0)
            .Select(join => join.EnableJoinNumber)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (enableJoinNumbers.Length == 0)
        {
            return;
        }

        var snapshots = await db.JoinSnapshots
            .Where(snapshot =>
                snapshot.ControllerId == controllerId &&
                snapshot.Kind == JoinKind.Digital &&
                enableJoinNumbers.Contains(snapshot.JoinNumber))
            .ToListAsync(cancellationToken);
        foreach (var snapshot in snapshots)
        {
            snapshot.DigitalValue = false;
        }
    }
}
