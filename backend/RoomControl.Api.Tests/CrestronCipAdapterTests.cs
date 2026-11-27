using System.Net;
using System.Net.Sockets;
using RoomControl.Api.Integrations;
using RoomControl.Api.Models;

namespace RoomControl.Api.Tests;

/// <summary>使用本机 TCP 模拟器验证 CIP Raw 连接握手、命令发送和回报处理。</summary>
public sealed class CrestronCipAdapterTests
{
    [Fact]
    public async Task RawSession_RegistersSendsCommandsAndRefreshesJoinState()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var serverTask = RunRawSimulatorAsync(listener, timeout.Token);
        await using var adapter = new CrestronCipAdapter(CreateController(port));
        var receivedUpdate = new TaskCompletionSource<AdapterJoinUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCompleted = new TaskCompletionSource<IReadOnlyCollection<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.JoinUpdated += update =>
        {
            receivedUpdate.TrySetResult(update);
            return Task.CompletedTask;
        };
        adapter.StateRefreshCompleted += reported =>
        {
            refreshCompleted.TrySetResult(reported);
            return Task.CompletedTask;
        };

        await adapter.ConnectAsync(timeout.Token);
        var receiveTask = adapter.RunAsync(timeout.Token);
        await adapter.SendDigitalPulseAsync(5, timeout.Token);
        await adapter.RequestStateAsync(timeout.Token);

        var update = await receivedUpdate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new AdapterJoinUpdate(5, true), update);
        var reported = await refreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(5, reported);
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiveTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serverTask);
    }

    [Fact]
    public async Task ConnectAsync_RejectsUnsupportedLegacyFraming()
    {
        await using var adapter = new CrestronCipAdapter(new Controller
        {
            Host = "127.0.0.1",
            Port = 41794,
            Framing = (CipFraming)1
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConnectAsync(CancellationToken.None));
        Assert.Contains("仅支持 CIP Raw", error.Message);
    }

    private static Controller CreateController(int port) => new()
    {
        Name = "Local simulator",
        Host = "127.0.0.1",
        Port = port,
        Ipid = 3
    };

    private static async Task RunRawSimulatorAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        // 模拟器按实际报文顺序完成注册、接收脉冲、状态请求并回报 Join。
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 0x0F, 0x00, 0x01, 0x02 }, cancellationToken);

        var signOn = await ReadRawMessageAsync(stream, cancellationToken);
        Assert.Equal(0x0A, signOn[0]);
        Assert.Equal(3, signOn[4]);
        await stream.WriteAsync(new byte[] { 0x02, 0x00, 0x04, 0x00, 0x00, 0x00, 0x1F }, cancellationToken);
        Assert.Equal(CipProtocolCodec.BuildUpdateRequest(), await ReadRawMessageAsync(stream, cancellationToken));
        Assert.Equal(new byte[] { 0x05, 0x00, 0x06, 0x00, 0x00, 0x03, 0x27, 0x04, 0x00 },
            await ReadRawMessageAsync(stream, cancellationToken));
        Assert.Equal(new byte[] { 0x05, 0x00, 0x06, 0x00, 0x00, 0x03, 0x27, 0x04, 0x80 },
            await ReadRawMessageAsync(stream, cancellationToken));
        Assert.Equal(CipProtocolCodec.BuildUpdateRequest(), await ReadRawMessageAsync(stream, cancellationToken));
        await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x06, 0x00, 0x00, 0x03, 0x00, 0x04, 0x00 },
            cancellationToken);
        await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x05, 0x00, 0x00, 0x02, 0x03, 0x16 },
            cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async Task<byte[]> ReadRawMessageAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[3];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var message = new byte[header.Length + header[2]];
        header.CopyTo(message, 0);
        await stream.ReadExactlyAsync(message.AsMemory(header.Length), cancellationToken);
        return message;
    }

}
