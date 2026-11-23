namespace RoomControl.Api.Models;

/// <summary>教室连接的中控设备及其 CIP 网络、在线状态和 Join 映射。</summary>
public sealed class Controller
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string AdapterType { get; set; } = "crestron-cip";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 41794;
    public CipFraming Framing { get; set; } = CipFraming.Raw;
    public int Ipid { get; set; } = 3;
    public bool Enabled { get; set; } = true;
    public bool IsOnline { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<JoinMapping> JoinMappings { get; set; } = new List<JoinMapping>();
}

/// <summary>当前支持的 CIP 报文封装方式。</summary>
public enum CipFraming
{
    Raw
}
