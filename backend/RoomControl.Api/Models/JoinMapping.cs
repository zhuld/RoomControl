namespace RoomControl.Api.Models;

/// <summary>将界面控制项关联到中控发送、状态反馈和可选使能 Join。</summary>
public sealed class JoinMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ControllerId { get; set; }
    public Controller Controller { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public JoinKind Kind { get; set; }
    public int SendJoinNumber { get; set; }
    public int StateJoinNumber { get; set; }
    // 0 表示未配置使能 Join，此时默认允许操作
    public int EnableJoinNumber { get; set; }
    // 删除该控制点前是否弹出确认弹窗
    public bool ShowConfirmationPopup { get; set; } = true;
    // 控制台按钮的显示顺序，由配置页拖拽排序维护
    public int SortOrder { get; set; }
}

/// <summary>Join 的信号类型；当前仅支持数字 Join。</summary>
public enum JoinKind
{
    Digital
}
