namespace RoomControl.Api.Models;

/// <summary>中控最近一次上报的 Join 值及其上报时间；数字状态只有高低两种，未上报时按低电平处理。</summary>
public sealed class JoinSnapshot
{
    public Guid ControllerId { get; set; }
    public JoinKind Kind { get; set; }
    public int JoinNumber { get; set; }
    public bool DigitalValue { get; set; }
    // 最近一次收到设备上报的时间，界面据此显示状态新鲜度。
    public DateTimeOffset LastReportedAtUtc { get; set; }
}
