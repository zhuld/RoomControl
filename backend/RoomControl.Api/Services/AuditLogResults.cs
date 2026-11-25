using Microsoft.AspNetCore.Http;

namespace RoomControl.Api.Services;

/// <summary>后台与批量下发没有 HTTP 响应，用与接口一致的结果码写入操作记录。</summary>
public static class AuditLogResults
{
    public const int CommandAccepted = StatusCodes.Status202Accepted;
    public const int TargetMissing = StatusCodes.Status409Conflict;
    public const int UnexpectedFailure = StatusCodes.Status500InternalServerError;

    public static int FromException(Exception exception) => exception switch
    {
        KeyNotFoundException => StatusCodes.Status404NotFound,
        ArgumentException => StatusCodes.Status400BadRequest,
        ControllerUnavailableException => StatusCodes.Status503ServiceUnavailable,
        InvalidOperationException => StatusCodes.Status409Conflict,
        _ => UnexpectedFailure,
    };
}
