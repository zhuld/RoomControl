using RoomControl.Api.Models;
using RoomControl.Api.Services;

namespace RoomControl.Api.Tests;

/// <summary>验证请求路径到日志分类的映射：自动任务与手动任务的改动同属「任务」分类。</summary>
public sealed class AuditLogCategoryTests
{
    [Theory]
    [InlineData("/api/manual-tasks", AuditLogCategories.Schedule)]
    [InlineData("/api/manual-tasks/abc", AuditLogCategories.Schedule)]
    [InlineData("/api/manual-tasks/abc/run", AuditLogCategories.Control)]
    [InlineData("/api/schedules", AuditLogCategories.Schedule)]
    [InlineData("/api/schedules/groups/abc/pause", AuditLogCategories.Schedule)]
    [InlineData("/api/joins/abc/pulse", AuditLogCategories.Control)]
    [InlineData("/api/auth/login", AuditLogCategories.Authentication)]
    [InlineData("/api/rooms/abc", AuditLogCategories.Configuration)]
    public void ResolvesCategoryFromPath(string path, string expected)
    {
        Assert.Equal(expected, AuditLogMiddleware.GetCategory(path));
    }
}
