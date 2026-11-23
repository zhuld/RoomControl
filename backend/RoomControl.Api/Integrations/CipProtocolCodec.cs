using RoomControl.Api.Models;

namespace RoomControl.Api.Integrations;

/// <summary>已拆出的 CIP Raw 消息类型与负载；TCP 帧头在拆包时已移除。</summary>
internal sealed record CipMessage(byte Type, byte[] Payload);

/// <summary>构造并解析当前适配器使用的 CIP Raw 消息，不处理 TCP 连接生命周期。</summary>
internal static class CipProtocolCodec
{
    private const byte JoinEvent = 0x05;
    private const byte DigitalJoin = 0x00;
    private const byte DigitalButtonJoin = 0x27;
    private const byte UpdateJoin = 0x03;
    private const byte UpdateEnded = 0x16;

    public static byte[] BuildSignOn(int ipid)
    {
        if (ipid is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(ipid), "CIP IPID must be between 0 and 255.");
        }

        var payload = new byte[11];
        payload[1] = (byte)ipid;
        return BuildMessage(0x0A, payload);
    }

    public static byte[] BuildUpdateRequest() => BuildJoinEvent(0x03, [0x00]);

    public static byte[] BuildMessage(byte type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "A raw CIP payload cannot exceed 255 bytes.");
        }

        var message = new byte[payload.Length + 3];
        message[0] = type;
        message[1] = 0;
        message[2] = (byte)payload.Length;
        payload.CopyTo(message.AsSpan(3));
        return message;
    }

    public static byte[] BuildDigitalPulseCommand(int joinNumber)
    {
        if (joinNumber is < 1 or > 32768)
        {
            throw new ArgumentOutOfRangeException(nameof(joinNumber), "Join number must be between 1 and 32768.");
        }

        // CIP 在报文中使用从 0 开始的 Join 索引，并以按下/释放两帧组成脉冲。
        var zeroBasedJoin = joinNumber - 1;
        var joinLowByte = (byte)zeroBasedJoin;
        var joinHighByte = (byte)((zeroBasedJoin >> 8) & 0x7F);
        var press = BuildJoinEvent(DigitalButtonJoin, [joinLowByte, joinHighByte]);
        var release = BuildJoinEvent(DigitalButtonJoin, [joinLowByte, (byte)(joinHighByte | 0x80)]);
        var pulse = new byte[press.Length + release.Length];
        press.CopyTo(pulse, 0);
        release.CopyTo(pulse, press.Length);
        return pulse;
    }

    public static AdapterJoinUpdate? ParseJoinUpdate(CipMessage message)
    {
        if (message.Type != JoinEvent || message.Payload.Length < 4)
        {
            return null;
        }

        // Payload[2] 含 Join 类型字节；扣除后只解析其后的 Join 数据。
        var payload = message.Payload;
        var dataLength = Math.Min(payload.Length - 4, Math.Max(0, payload[2] - 1));
        var data = payload.AsSpan(4, dataLength);

        if (payload[3] == DigitalJoin && data.Length >= 2)
        {
            var join = (data[0] | ((data[1] & 0x7F) << 8)) + 1;
            return new AdapterJoinUpdate(join, (data[1] & 0x80) == 0);
        }

        return null;
    }

    public static bool IsUpdateEnded(CipMessage message) =>
        message.Type == JoinEvent &&
        message.Payload.Length >= 5 &&
        message.Payload[3] == UpdateJoin &&
        message.Payload[4] == UpdateEnded;

    public static bool IsAccepted(CipMessage message) =>
        message.Type == 0x02 &&
        message.Payload.Length == 4 &&
        message.Payload[0] == 0 &&
        message.Payload[1] == 0 &&
        message.Payload[2] == 0 &&
        message.Payload[3] == 0x1F;

    public static bool IsRefused(CipMessage message) =>
        message.Type == 0x02 &&
        message.Payload.Length == 3 &&
        message.Payload[0] == 0xFF &&
        message.Payload[1] == 0xFF &&
        message.Payload[2] == 0x02;

    private static byte[] BuildJoinEvent(byte joinType, ReadOnlySpan<byte> data)
    {
        var payload = new byte[data.Length + 4];
        payload[2] = checked((byte)(data.Length + 1));
        payload[3] = joinType;
        data.CopyTo(payload.AsSpan(4));
        return BuildMessage(JoinEvent, payload);
    }
}
