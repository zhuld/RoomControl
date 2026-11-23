using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>统一日志中对象与电平的显示格式。</summary>
public static class AuditLogFormatting
{
    public static string FormatRoom(Room room)
    {
        var parts = new[] { room.Building, room.Floor, room.Name }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        return $"教室「{string.Join(" · ", parts)}」";
    }

    public static string FormatJoinTarget(Room? room, IReadOnlyCollection<string> joinNames, int joinNumber)
    {
        var pointLabel = joinNames.Count > 0
            ? $"控制点「{string.Join("、", joinNames)}」"
            : $"Join {joinNumber}";
        return room is null ? pointLabel : $"{FormatRoom(room)}· {pointLabel}";
    }

    public static string FormatLevel(bool? value) => value switch
    {
        true => "高电平",
        false => "低电平",
        _ => "未上报",
    };
}
