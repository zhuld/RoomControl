using System.Net.Sockets;
using RoomControl.Api.Models;

namespace RoomControl.Api.Integrations;

/// <summary>通过 TCP CIP Raw 与 Crestron 中控交互，并按完整帧解析 Join 更新。</summary>
public sealed class CrestronCipAdapter(Controller controller) : IControllerAdapter
{
    private readonly TcpClient _client = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NetworkStream? _stream;

    public event Func<AdapterJoinUpdate, Task>? JoinUpdated;
    public event Func<IReadOnlyCollection<int>, Task>? StateRefreshCompleted;
    private readonly HashSet<int> reportedJoinNumbers = [];
    private readonly Lock reportedLock = new();

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (controller.Framing != CipFraming.Raw)
        {
            throw new InvalidOperationException("Only CIP Raw framing is supported.");
        }

        await _client.ConnectAsync(controller.Host, controller.Port, cancellationToken);
        _stream = _client.GetStream();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        // 只有收到主动连接请求后才发送 Sign On，并等待设备接受该 IPID。
        var registered = false;
        while (!registered)
        {
            var message = await ReadNextMessageAsync(timeout.Token);
            if (message.Type == 0x0F)
            {
                await WriteCipMessageAsync(CipProtocolCodec.BuildSignOn(controller.Ipid), timeout.Token);
            }
            else if (CipProtocolCodec.IsAccepted(message))
            {
                registered = true;
            }
            else if (CipProtocolCodec.IsRefused(message))
            {
                throw new InvalidOperationException($"CIP registration was refused for IPID {controller.Ipid}.");
            }
        }

        await RequestStateAsync(cancellationToken);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 长连接循环负责心跳应答、Join 更新转发和状态回放结束通知。
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadNextMessageAsync(cancellationToken);
            switch (message.Type)
            {
                case 0x0D:
                    await WriteCipMessageAsync([0x0E, 0x00, 0x02, 0x00, 0x00], cancellationToken);
                    break;
                case 0x05:
                    var update = CipProtocolCodec.ParseJoinUpdate(message);
                    if (update is not null)
                    {
                        lock (reportedLock)
                        {
                            reportedJoinNumbers.Add(update.JoinNumber);
                        }
                        if (JoinUpdated is { } joinUpdated)
                        {
                            await joinUpdated(update);
                        }
                    }
                    else if (CipProtocolCodec.IsUpdateEnded(message))
                    {
                        int[] reported;
                        lock (reportedLock)
                        {
                            reported = reportedJoinNumbers.ToArray();
                            reportedJoinNumbers.Clear();
                        }
                        if (StateRefreshCompleted is { } refreshCompleted)
                        {
                            await refreshCompleted(reported);
                        }
                    }
                    break;
            }
        }
    }

    public Task SendDigitalPulseAsync(int joinNumber, CancellationToken cancellationToken) =>
        WriteRawAsync(CipProtocolCodec.BuildDigitalPulseCommand(joinNumber), cancellationToken);

    public Task RequestStateAsync(CancellationToken cancellationToken)
    {
        // 每轮回放单独记录上报过的 Join，结束时用于清除过期高电平快照。
        lock (reportedLock)
        {
            reportedJoinNumbers.Clear();
        }
        return WriteCipMessageAsync(CipProtocolCodec.BuildUpdateRequest(), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _writeLock.Dispose();
        await Task.CompletedTask;
    }

    private async Task<CipMessage> ReadNextMessageAsync(CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("The CIP adapter is not connected.");
        // TCP 是字节流，必须先读满固定长度头部，再按头部长度读完负载。
        var header = new byte[3];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (header[1] != 0)
        {
            throw new InvalidDataException("Invalid raw CIP frame header.");
        }

        var payload = new byte[header[2]];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new CipMessage(header[0], payload);
    }

    private async Task WriteCipMessageAsync(byte[] message, CancellationToken cancellationToken)
    {
        await WriteRawAsync(message, cancellationToken);
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("The CIP adapter is not connected.");
        // 多个调用方可能同时发心跳应答和控制命令，写锁避免报文交错。
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(data, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
