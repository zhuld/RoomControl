using Microsoft.AspNetCore.Routing;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>记录 API 写操作与登录尝试，不保存请求正文或凭据。</summary>
public sealed class AuditLogMiddleware(
    RequestDelegate next,
    AuditLogWriter auditLogWriter,
    ILogger<AuditLogMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, AuditLogTargetResolver targetResolver)
    {
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? string.Empty;
        if (!IsAuditedRequest(method, path))
        {
            await next(context);
            return;
        }

        var category = GetCategory(path);
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? path;
        // 在操作执行前解析对象，删除请求也能记录被删除的控制点/教室名称。
        var routeTarget = await ResolveTargetAsync(context, targetResolver);

        context.Response.OnStarting(async () =>
        {
            try
            {
                // 创建类请求优先记录新建对象（响应 Location），否则记录路由中的对象。
                var createdTarget = await ResolveLocationAsync(context, targetResolver);
                var target = createdTarget ?? routeTarget;
                // 认证类请求：操作者记录成功登录的账号，对象记录来源 IP。
                var isAuth = category == AuditLogCategories.Authentication;
                var targetLabel = isAuth
                    ? AuditLogAuthTarget.Build(context)
                    : target?.Label ?? string.Empty;
                var actor = isAuth
                    ? AuditLogAuthTarget.ResolveActor(context)
                    : context.User.Identity?.Name ?? "anonymous";
                await auditLogWriter.WriteAsync(
                    category,
                    AuditLogActions.Resolve(method, route, target?.Detail),
                    targetLabel,
                    actor,
                    context.Response.StatusCode);
            }
            catch (Exception exception)
            {
                // 日志失败不能影响原请求的响应。
                logger.LogWarning(exception, "Failed to write audit log entry for {Method} {Path}.", method, path);
            }
        });

        await next(context);
        if (!context.Response.HasStarted)
        {
            await context.Response.StartAsync(context.RequestAborted);
        }
    }

    private async Task<AuditTarget?> ResolveTargetAsync(HttpContext context, AuditLogTargetResolver targetResolver)
    {
        // 日志是旁路能力：解析失败时只记录动作，不影响原请求。
        try
        {
            return await targetResolver.ResolveAsync(context);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to resolve audit log target.");
            return null;
        }
    }

    private async Task<AuditTarget?> ResolveLocationAsync(HttpContext context, AuditLogTargetResolver targetResolver)
    {
        try
        {
            return await targetResolver.ResolveLocationAsync(context.Response.Headers.Location.ToString());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to resolve audit log target from response location.");
            return null;
        }
    }

    private static bool IsAuditedRequest(string method, string path) =>
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
        (HttpMethods.IsPost(method) ||
         HttpMethods.IsPut(method) ||
         HttpMethods.IsPatch(method) ||
         HttpMethods.IsDelete(method));

    private static string GetCategory(string path)
    {
        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
        {
            return AuditLogCategories.Authentication;
        }
        if (path.StartsWith("/api/schedules", StringComparison.OrdinalIgnoreCase))
        {
            return AuditLogCategories.Schedule;
        }
        if (path.EndsWith("/pulse", StringComparison.OrdinalIgnoreCase))
        {
            return AuditLogCategories.Control;
        }
        return AuditLogCategories.Configuration;
    }
}
