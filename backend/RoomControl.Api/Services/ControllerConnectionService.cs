using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Data;
using RoomControl.Api.Integrations;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>后台协调启用的中控连接，并为每台设备管理独立重连工作循环。</summary>
public sealed class ControllerConnectionService(
    IDbContextFactory<ControlDbContext> contextFactory,
    IControllerAdapterFactory adapterFactory,
    ControllerStateStore stateStore,
    IHostApplicationLifetime applicationLifetime,
    ILogger<ControllerConnectionService> logger) : BackgroundService, IControlCommandSender
{
    private readonly ConcurrentDictionary<Guid, IControllerAdapter> _connectedAdapters = new();
    private readonly Dictionary<Guid, WorkerHandle> _workers = [];

    /// <summary>
    /// 发送一次数字脉冲。手动操作受使能 Join 限制；自动任务忽略使能，只要求中控在线。
    /// </summary>
    public async Task SendDigitalPulseAsync(
        Guid mappingId,
        bool enforceEnableJoin,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var mapping = await db.JoinMappings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == mappingId, cancellationToken)
            ?? throw new KeyNotFoundException("Join mapping was not found.");

        if (mapping.Kind != JoinKind.Digital)
        {
            throw new ArgumentException("A digital join is required for a pulse command.");
        }

        if (!_connectedAdapters.TryGetValue(mapping.ControllerId, out var adapter))
        {
            throw new ControllerUnavailableException("The room controller is offline.");
        }

        // 配置了使能 Join 时只有高电平才允许手动操作；未上报按低电平处理，直接拒绝。
        if (enforceEnableJoin && mapping.EnableJoinNumber > 0)
        {
            var isEnabled = await db.JoinSnapshots.AsNoTracking()
                .AnyAsync(snapshot =>
                    snapshot.ControllerId == mapping.ControllerId &&
                    snapshot.Kind == JoinKind.Digital &&
                    snapshot.JoinNumber == mapping.EnableJoinNumber &&
                    snapshot.DigitalValue,
                    cancellationToken);
            if (!isEnabled)
            {
                throw new InvalidOperationException("The control point is disabled until its enable Join reports high.");
            }
        }

        try
        {
            await adapter.SendDigitalPulseAsync(mapping.SendJoinNumber, cancellationToken);
            await adapter.RequestStateAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            throw new ControllerUnavailableException(
                "The controller connection ended while sending or refreshing state; verify the reported device state.",
                exception);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等宿主完成启动再连中控：启动阶段服务尚未就绪，过早连接会与数据库初始化争用，
        // 并可能在中控真正可用前写入误导性的连接记录。
        await WaitForApplicationStartedAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // 进程重启后内存连接均已断开，先修正数据库中的在线状态，再周期性对齐配置。
        try
        {
            await stateStore.MarkAllOfflineAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to reset persisted controller states during startup.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileWorkersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to reconcile controller connections.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        foreach (var controllerId in _workers.Keys.ToArray())
        {
            await StopWorkerAsync(controllerId);
        }
    }

    /// <summary>宿主启动完成（服务已监听、后台任务已就绪）后才继续；停机时立即返回。</summary>
    private async Task WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        // ApplicationStarted 在全部托管服务启动后触发，因此这里等待不会阻塞宿主启动本身。
        if (applicationLifetime.ApplicationStarted.IsCancellationRequested)
        {
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            applicationLifetime.ApplicationStarted);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // 启动完成或进程停机都会走到这里，由调用方按 stoppingToken 判断。
        }
    }

    private async Task ReconcileWorkersAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var controllers = await db.Controllers.AsNoTracking()
            .Where(item => item.Enabled)
            .ToListAsync(cancellationToken);
        var currentIds = controllers.Select(item => item.Id).ToHashSet();

        // 删除、停用或连接参数变化时先停止旧任务，再按最新配置创建工作任务。
        foreach (var removedId in _workers.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            await StopWorkerAsync(removedId);
        }

        foreach (var controller in controllers)
        {
            var signature = GetSignature(controller);
            if (_workers.TryGetValue(controller.Id, out var existing) &&
                (existing.Signature != signature || existing.Task.IsCompleted))
            {
                await StopWorkerAsync(controller.Id);
            }

            if (!_workers.ContainsKey(controller.Id))
            {
                var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var task = RunControllerAsync(controller, workerCancellation.Token);
                _workers[controller.Id] = new WorkerHandle(signature, workerCancellation, task);
            }
        }
    }

    private async Task RunControllerAsync(Controller controller, CancellationToken cancellationToken)
    {
        // 网络故障采用有上限的指数退避，避免离线设备导致紧密重试。
        var delay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            IControllerAdapter? adapter = null;
            try
            {
                adapter = adapterFactory.Create(controller);
                adapter.JoinUpdated += update => PersistJoinUpdateAsync(controller.Id, update, cancellationToken);
                adapter.StateRefreshCompleted += reported => ReconcileStateAsync(controller.Id, reported, cancellationToken);
                await adapter.ConnectAsync(cancellationToken);
                _connectedAdapters[controller.Id] = adapter;
                await stateStore.SetConnectionAsync(controller.Id, true, null, cancellationToken);
                delay = TimeSpan.FromSeconds(2);
                await adapter.RunAsync(cancellationToken);
                throw new IOException("The CIP connection ended unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Controller {ControllerId} connection failed; retrying.", controller.Id);
                await SetConnectionSafelyAsync(controller.Id, false, exception.Message);
            }
            finally
            {
                _connectedAdapters.TryRemove(controller.Id, out _);
                if (adapter is not null)
                {
                    await adapter.DisposeAsync();
                }
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        await SetConnectionSafelyAsync(controller.Id, false, "连接已停止。");
    }

    private async Task StopWorkerAsync(Guid controllerId)
    {
        if (!_workers.Remove(controllerId, out var worker))
        {
            return;
        }

        // 取消后等待工作任务退出，确保适配器和取消令牌不会被过早释放。
        worker.Cancellation.Cancel();
        try
        {
            await worker.Task;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Controller worker {ControllerId} ended unexpectedly.", controllerId);
        }
        finally
        {
            worker.Cancellation.Dispose();
        }

        await SetConnectionSafelyAsync(controllerId, false, "连接已停止。");
    }

    private async Task SetConnectionSafelyAsync(Guid controllerId, bool isOnline, string? error)
    {
        try
        {
            await stateStore.SetConnectionAsync(controllerId, isOnline, error, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist connection state for controller {ControllerId}.", controllerId);
        }
    }

    private async Task PersistJoinUpdateAsync(Guid controllerId, AdapterJoinUpdate update, CancellationToken cancellationToken)
    {
        try
        {
            await stateStore.SaveJoinUpdateAsync(controllerId, update, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            stateStore.LogUpdateFailure(controllerId, exception);
        }
    }

    private async Task ReconcileStateAsync(
        Guid controllerId,
        IReadOnlyCollection<int> reportedJoinNumbers,
        CancellationToken cancellationToken)
    {
        try
        {
            await stateStore.ReconcileStateSnapshotsAsync(controllerId, reportedJoinNumbers, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            stateStore.LogUpdateFailure(controllerId, exception);
        }
    }

    private static string GetSignature(Controller controller) =>
        $"{controller.AdapterType}|{controller.Host}|{controller.Port}|{controller.Ipid}";

    private sealed record WorkerHandle(string Signature, CancellationTokenSource Cancellation, Task Task);
}
