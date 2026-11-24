using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RoomControl.Api.Contracts;
using RoomControl.Api.Data;
using RoomControl.Api.Models;

namespace RoomControl.Api.Services;

/// <summary>负责用户认证、密码哈希、角色维护和账号生命周期校验。</summary>
public sealed class UserService
{
    private const int Iterations = 210_000;
    private const int MinPasswordLength = 6;
    // 上限同时约束密码设置与登录校验，避免超长输入驱动 PBKDF2 消耗大量 CPU。
    private const int MaxPasswordLength = 128;
    private const int MaxUsernameLength = 60;
    private static readonly byte[] PlaceholderSalt = RandomNumberGenerator.GetBytes(16);
    private static readonly byte[] PlaceholderHash = RandomNumberGenerator.GetBytes(32);
    private readonly ControlDbContext _db;

    public UserService(ControlDbContext db)
    {
        _db = db;
    }

    /// <summary>校验凭据；成功返回当前角色与规范化用户名，失败或账号停用返回 <c>null</c>。</summary>
    public (UserRole Role, string Username)? Authenticate(string? username, string? password)
    {
        var user = FindActiveUser(username, password);
        return user is null ? null : (user.Role, user.Username);
    }

    /// <summary>用于 Cookie 校验：返回账号当前生效的角色，账号被删除或禁用时返回 null。</summary>
    public UserRole? GetActiveRole(string username) =>
        _db.Users.AsNoTracking()
            .Where(u => u.Username == username && u.IsActive)
            .Select(u => (UserRole?)u.Role)
            .SingleOrDefault();

    /// <summary>按用户名升序返回账号的安全字段，不包含密码哈希或盐。</summary>
    public IEnumerable<UserDto> GetUsers()
    {
        return _db.Users.AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new UserDto(
                u.Id,
                u.Username,
                u.Role.ToName(),
                u.Role.DisplayName(),
                u.IsActive,
                u.CreatedAt));
    }

    /// <summary>登录页账号下拉框：仅返回启用账号的用户名与角色，不暴露其他信息。</summary>
    public IEnumerable<LoginAccountDto> GetLoginAccounts()
    {
        return _db.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.Username)
            .Select(u => new LoginAccountDto(u.Username, u.Role.ToName()))
            .ToList();
    }

    /// <summary>创建账号；用户名全局唯一，密码至少 6 个字符，角色必须是受支持值。</summary>
    public UserDto CreateUser(CreateUserRequest request)
    {
        var username = NormalizeUsername(request.Username);
        var role = ParseRole(request.Role);
        var password = request.Password ?? string.Empty;
        EnsurePasswordLength(password);

        if (_db.Users.Any(u => u.Username == username))
        {
            throw new InvalidOperationException($"用户 '{username}' 已存在。");
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var user = new User
        {
            Username = username,
            PasswordHash = HashPassword(password, salt),
            PasswordSalt = salt,
            Role = role,
            IsActive = request.IsActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.Users.Add(user);
        _db.SaveChanges();

        return new UserDto(
            user.Id,
            user.Username,
            user.Role.ToName(),
            user.Role.DisplayName(),
            user.IsActive,
            user.CreatedAt);
    }

    /// <summary>
    /// 部分更新账号：<c>null</c> 字段保持原值。拒绝修改自己的角色或禁用自己，
    /// 并保证至少保留一名启用的管理员。账号不存在时返回 <c>null</c>。
    /// </summary>
    public UserDto? UpdateUser(Guid id, UpdateUserRequest request, string currentUsername)
    {
        var user = _db.Users.SingleOrDefault(u => u.Id == id);
        if (user is null)
        {
            return null;
        }

        var newRole = request.Role is null ? (UserRole?)null : ParseRole(request.Role);
        var isSelf = string.Equals(user.Username, currentUsername, StringComparison.Ordinal);
        if (isSelf)
        {
            if (newRole is not null && newRole != user.Role)
            {
                throw new InvalidOperationException("不能修改自己的角色。");
            }
            if (request.IsActive is false)
            {
                throw new InvalidOperationException("不能禁用自己的账号。");
            }
        }

        // 禁用或降级管理员之前，确保数据库中仍至少有一名启用管理员。
        var losesAdmin = user.Role == UserRole.Admin &&
            user.IsActive &&
            ((newRole is not null && newRole != UserRole.Admin) || request.IsActive is false);
        if (losesAdmin &&
            !_db.Users.Any(u => u.Id != user.Id && u.Role == UserRole.Admin && u.IsActive))
        {
            throw new InvalidOperationException("至少需要保留一名启用的管理员账号。");
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            EnsurePasswordLength(request.Password);
            var salt = RandomNumberGenerator.GetBytes(16);
            user.PasswordHash = HashPassword(request.Password, salt);
            user.PasswordSalt = salt;
        }

        if (newRole is not null)
        {
            user.Role = newRole.Value;
        }

        if (request.IsActive is not null)
        {
            user.IsActive = request.IsActive.Value;
        }

        user.UpdatedAt = DateTimeOffset.UtcNow;
        _db.SaveChanges();

        return new UserDto(
            user.Id,
            user.Username,
            user.Role.ToName(),
            user.Role.DisplayName(),
            user.IsActive,
            user.CreatedAt);
    }

    /// <summary>删除账号；拒绝删除自己或最后一名启用的管理员。账号不存在时返回 <c>false</c>。</summary>
    public bool DeleteUser(Guid id, string currentUsername)
    {
        var user = _db.Users.SingleOrDefault(u => u.Id == id);
        if (user is null)
        {
            return false;
        }

        if (string.Equals(user.Username, currentUsername, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("不能删除自己的账号。");
        }

        if (user.Role == UserRole.Admin &&
            user.IsActive &&
            !_db.Users.Any(u => u.Id != user.Id && u.Role == UserRole.Admin && u.IsActive))
        {
            throw new InvalidOperationException("至少需要保留一名启用的管理员账号。");
        }

        _db.Users.Remove(user);
        _db.SaveChanges();
        return true;
    }

    /// <summary>当前用户修改密码：旧密码不正确或账号停用时返回 <c>false</c>，新密码需 6–128 个字符。</summary>
    public bool ChangePassword(string username, string? oldPassword, string? newPassword)
    {
        var password = newPassword ?? string.Empty;
        EnsurePasswordLength(password);

        var user = _db.Users.SingleOrDefault(u => u.Username == username);
        if (user is null || !user.IsActive || !IsPasswordMatch(user, oldPassword))
        {
            return false;
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        user.PasswordHash = HashPassword(password, salt);
        user.PasswordSalt = salt;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        _db.SaveChanges();
        return true;
    }

    /// <summary>仅在数据库没有任何账号时创建初始管理员，后续启动不会覆盖已有密码。</summary>
    public void EnsureDefaultAdmin(string adminUsername, string adminPassword)
    {
        // 只在完全没有账号时创建初始管理员，不在后续启动时覆盖管理员改过的密码。
        if (string.IsNullOrWhiteSpace(adminUsername) || string.IsNullOrWhiteSpace(adminPassword))
        {
            return;
        }

        if (_db.Users.Any())
        {
            return;
        }

        if (adminPassword.Length is < MinPasswordLength or > MaxPasswordLength)
        {
            throw new InvalidOperationException(
                $"默认管理员密码长度需在 {MinPasswordLength}–{MaxPasswordLength} 个字符之间。");
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        _db.Users.Add(new User
        {
            Username = NormalizeUsername(adminUsername),
            PasswordHash = HashPassword(adminPassword, salt),
            PasswordSalt = salt,
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    // 只查询一次账号，并在账号不存在时同样执行 KDF，避免通过响应时间判断用户名是否存在。
    private User? FindActiveUser(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrEmpty(password) ||
            username.Trim().Length > MaxUsernameLength)
        {
            return null;
        }

        var user = _db.Users.AsNoTracking().SingleOrDefault(u => u.Username == username.Trim());
        return user is { IsActive: true } && IsPasswordMatch(user, password) ? user : null;
    }

    private static bool IsPasswordMatch(User? user, string? password)
    {
        // 超长密码直接拒绝，避免无效凭据驱动代价高昂的 KDF。
        if (password is { Length: > MaxPasswordLength })
        {
            return false;
        }

        // 不存在用户时使用固定占位盐和哈希，仍执行相同的 KDF 与固定时间比较。
        var salt = user?.PasswordSalt ?? PlaceholderSalt;
        var expectedHash = user?.PasswordHash ?? PlaceholderHash;
        var candidate = HashPassword(password ?? string.Empty, salt);
        return CryptographicOperations.FixedTimeEquals(candidate, expectedHash);
    }

    private static void EnsurePasswordLength(string password)
    {
        if (password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"密码长度至少需要 {MinPasswordLength} 个字符。");
        }
        if (password.Length > MaxPasswordLength)
        {
            throw new ArgumentException($"密码长度不能超过 {MaxPasswordLength} 个字符。");
        }
    }

    private static byte[] HashPassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static string NormalizeUsername(string? username)
    {
        var trimmed = username?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > MaxUsernameLength)
        {
            throw new ArgumentException($"用户名必填且不能超过 {MaxUsernameLength} 个字符。");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("用户名不能包含控制字符。");
        }

        return trimmed;
    }

    private static UserRole ParseRole(string? name) =>
        UserRoleExtensions.TryFromName(name, out var role)
            ? role
            : throw new ArgumentException("请选择有效的用户角色。");
}

/// <summary>用户管理接口返回的安全字段，不包含密码哈希或盐。</summary>
public sealed record UserDto(
    Guid Id,
    string Username,
    string Role,
    string RoleDisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>登录页账号选择列表返回的最小字段集。</summary>
public sealed record LoginAccountDto(
    string Username,
    string Role);
