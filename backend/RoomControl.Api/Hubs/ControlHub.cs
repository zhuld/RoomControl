using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RoomControl.Api.Services;

namespace RoomControl.Api.Hubs;

/// <summary>按房间分组推送实时状态；连接加入或离开分组前重新验证账号有效性。</summary>
[Authorize]
public sealed class ControlHub : Hub
{
    private readonly UserService _userService;

    public ControlHub(UserService userService)
    {
        _userService = userService;
    }

    public Task JoinRoom(Guid roomId) =>
        WithActiveUserAsync(() => Groups.AddToGroupAsync(Context.ConnectionId, $"room:{roomId}"));

    public Task LeaveRoom(Guid roomId) =>
        WithActiveUserAsync(() => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"room:{roomId}"));

    /// <summary>
    /// 长连接只在握手时校验 Cookie：账号被禁用或删除后，在这里断开而不是继续推送状态。
    /// </summary>
    private Task WithActiveUserAsync(Func<Task> action)
    {
        var username = Context.User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username) || _userService.GetActiveRole(username) is null)
        {
            Context.Abort();
            return Task.CompletedTask;
        }

        return action();
    }
}
