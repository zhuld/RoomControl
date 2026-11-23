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
    private static readonly byte[] PlaceholderSalt = RandomNumberGenerator.GetBytes(16);
    private static readonly byte[] PlaceholderHash = RandomNumberGenerator.GetBytes(32);
    private readonly ControlDbContext _db;

    public UserService(ControlDbContext db)
    {
        _db = db;
    }

    public (UserRole Role, string Username)? Authenticate(string? username, string? password)
    {
        if (!VerifyPassword(username, password))
        {
            return null;
        }

        var user = _db.Users.AsNoTracking().Single(u => u.Username == username!.Trim());
        return (user.Role, user.Username);
    }

    /// <summary>用于 Cookie 校验：返回账号当前生效的角色，账号被删除或禁用时返回 null。</summary>
    public UserRole? GetActiveRole(string username) =>
        _db.Users.AsNoTracking()
            .Where(u => u.Username == username && u.IsActive)
            .Select(u => (UserRole?)u.Role)
            .SingleOrDefault();

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

    public UserDto CreateUser(CreateUserRequest request)
    {
        var username = NormalizeUsername(request.Username);
        var role = ParseRole(request.Role);
        var password = request.Password ?? string.Empty;
        if (password.Length < 6)
        {
            throw new ArgumentException("密码长度至少需要 6 个字符。");
        }

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
            if (request.Password.Length < 6)
            {
                throw new ArgumentException("密码长度至少需要 6 个字符。");
            }
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

    public bool ChangePassword(string username, string? oldPassword, string? newPassword)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
        {
            throw new ArgumentException("新密码长度至少需要 6 个字符。");
        }

        var user = _db.Users.SingleOrDefault(u => u.Username == username);
        if (user is null || !user.IsActive || !IsPasswordMatch(user, oldPassword))
        {
            return false;
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        user.PasswordHash = HashPassword(newPassword, salt);
        user.PasswordSalt = salt;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        _db.SaveChanges();
        return true;
    }

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

        if (adminPassword.Length < 6)
        {
            throw new InvalidOperationException("默认管理员密码长度至少需要 6 个字符。");
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

    private bool VerifyPassword(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var user = _db.Users.SingleOrDefault(u => u.Username == username.Trim());
        // 账号不存在时同样计算一次哈希，避免通过响应时间判断用户名是否存在。
        var isMatch = IsPasswordMatch(user, password);
        return user is { IsActive: true } && isMatch;
    }

    private static bool IsPasswordMatch(User? user, string? password)
    {
        // 不存在用户时使用固定占位盐和哈希，仍执行相同的 KDF 与固定时间比较。
        var salt = user?.PasswordSalt ?? PlaceholderSalt;
        var expectedHash = user?.PasswordHash ?? PlaceholderHash;
        var candidate = HashPassword(password ?? string.Empty, salt);
        return CryptographicOperations.FixedTimeEquals(candidate, expectedHash);
    }

    private static byte[] HashPassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static string NormalizeUsername(string? username)
    {
        var trimmed = username?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > 60)
        {
            throw new ArgumentException("用户名必填且不能超过 60 个字符。");
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
