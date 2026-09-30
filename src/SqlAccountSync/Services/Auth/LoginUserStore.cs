using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;
using SqlAccountSync.Authorization;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Secrets;

namespace SqlAccountSync.Services.Auth;

public interface ILoginUserStore
{
    Task<AppUser?> FindActiveByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<AppUser?> GetByIdAsync(int userId, CancellationToken cancellationToken = default);
    Task<List<AppUser>> ListAsync(CancellationToken cancellationToken = default);
    Task<int> AddAsync(string email, string displayName, string role, string? mobile, CancellationToken cancellationToken = default);
    Task SetActiveAsync(int userId, bool active, CancellationToken cancellationToken = default);
    Task SetRoleAsync(int userId, string role, CancellationToken cancellationToken = default);
    Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Users live in the AWS login database <c>SY_USER</c>, not <c>SYNC.FDB</c>.</summary>
public sealed class LoginUserStore : ILoginUserStore
{
    private const string RoleColumn = "UDF_SYNCROLE";
    private const string CreatedColumn = "UDF_SYNCCREATED";
    private const string UserColumns = "AUTOKEY, TRIM(CODE), TRIM(NAME), TRIM(MOBILE), TRIM(EMAIL), ISACTIVE, TRIM(UDF_SYNCROLE), UDF_SYNCCREATED";
    private readonly ILoginDatabaseSource _source;
    private readonly ISecretResolver _secrets;
    private readonly IUserDirectory _syncUsers;
    private readonly CentralDbOptions _central;
    private bool _roleColumnReady;

    public LoginUserStore(
        ILoginDatabaseSource source,
        ISecretResolver secrets,
        IUserDirectory syncUsers,
        IOptions<CentralDbOptions> central)
    {
        _source = source;
        _secrets = secrets;
        _syncUsers = syncUsers;
        _central = central.Value;
    }

    public async Task<AppUser?> FindActiveByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadByEmailAsync(conn, email, cancellationToken).ConfigureAwait(false);
        if (row is null || !row.Active)
            return null;
        return await ToUserAsync(conn, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppUser?> GetByIdAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadByIdAsync(conn, userId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : await ToUserAsync(conn, row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<AppUser>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ReadAllAsync(conn, cancellationToken).ConfigureAwait(false);
        var users = new List<AppUser>(rows.Count);
        foreach (var row in rows)
            users.Add(await ToUserAsync(conn, row, cancellationToken).ConfigureAwait(false));
        return users.OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<int> AddAsync(string email, string displayName, string role, string? mobile, CancellationToken cancellationToken = default)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        displayName = (displayName ?? "").Trim();
        mobile = (mobile ?? "").Trim();
        role = AppRoles.Normalize(role);
        if (email.Length == 0 || !email.Contains('@', StringComparison.Ordinal))
            throw new InvalidOperationException("Enter a valid email.");
        if (displayName.Length == 0)
            throw new InvalidOperationException("Enter a name.");

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await EmailExistsAsync(conn, email, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("That email is already in the login database.");

        var id = await NextIdAsync(conn, cancellationToken).ConfigureAwait(false);
        var code = await UniqueCodeAsync(conn, email, cancellationToken).ConfigureAwait(false);
        await InsertAsync(conn, id, code, displayName, mobile, email, role, cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task SetActiveAsync(int userId, bool active, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadByIdAsync(conn, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("User not found.");
        var user = await ToUserAsync(conn, row, cancellationToken).ConfigureAwait(false);
        if (!active && IsStoredAdmin(user) && await CountOtherActiveAdminsAsync(conn, userId, cancellationToken).ConfigureAwait(false) == 0)
            throw new InvalidOperationException("The last ADMIN must stay on.");

        await ExecuteAsync(conn, "UPDATE SY_USER SET ISACTIVE = @Active WHERE AUTOKEY = @Id", cancellationToken,
            ("@Active", active), ("@Id", userId)).ConfigureAwait(false);
    }

    public async Task SetRoleAsync(int userId, string role, CancellationToken cancellationToken = default)
    {
        role = AppRoles.Normalize(role);
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadByIdAsync(conn, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("User not found.");
        var user = await ToUserAsync(conn, row, cancellationToken).ConfigureAwait(false);
        if (user.IsActive && IsStoredAdmin(user) && role != AppRoles.Admin
            && await CountOtherActiveAdminsAsync(conn, userId, cancellationToken).ConfigureAwait(false) == 0)
            throw new InvalidOperationException("The last ADMIN must stay on.");

        await WriteRoleAsync(conn, userId, role, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ReadAllAsync(conn, cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var row in rows)
        {
            if (!row.Active) continue;
            var user = await ToUserAsync(conn, row, cancellationToken).ConfigureAwait(false);
            if (IsStoredAdmin(user)) count++;
        }
        return count;
    }

    private async Task<AppUser> ToUserAsync(FbConnection conn, RawUser row, CancellationToken cancellationToken)
    {
        var role = await ResolveRoleAsync(conn, row, cancellationToken).ConfigureAwait(false);
        var name = row.Name.Length > 0 ? row.Name : row.Code;
        if (name.Length == 0) name = row.Email;
        return new AppUser
        {
            UserId = row.Id,
            Email = row.Email,
            Code = row.Code,
            Mobile = row.Mobile,
            DisplayName = name,
            Role = role,
            IsActive = row.Active,
            CreatedAt = row.Created ?? default
        };
    }

    private async Task<string> ResolveRoleAsync(FbConnection conn, RawUser row, CancellationToken cancellationToken)
    {
        if (IsKnownRole(row.Role))
            return AppRoles.Normalize(row.Role);

        var copied = await CopySyncRoleAsync(row.Email, cancellationToken).ConfigureAwait(false);
        var role = copied ?? AppRoles.Viewer;
        await WriteRoleAsync(conn, row.Id, role, cancellationToken).ConfigureAwait(false);
        return role;
    }

    private async Task<string?> CopySyncRoleAsync(string email, CancellationToken cancellationToken)
    {
        if (email.Length == 0 || !email.Contains('@', StringComparison.Ordinal))
            return null;
        var existing = await _syncUsers.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        return existing is null ? null : AppRoles.Normalize(existing.Role);
    }

    private async Task<FbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var db = await _source.RequireAsync(cancellationToken).ConfigureAwait(false);
        var password = await PasswordAsync(cancellationToken).ConfigureAwait(false);
        var csb = new FbConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(db.DbHost) ? "localhost" : db.DbHost.Trim(),
            Port = db.DbPort <= 0 ? 3050 : db.DbPort,
            Database = db.DbPath.Replace('\\', '/'),
            UserID = string.IsNullOrWhiteSpace(_central.User) ? "SYSDBA" : _central.User,
            Password = password,
            Charset = string.IsNullOrWhiteSpace(db.DbCharset) ? "UTF8" : db.DbCharset,
            Dialect = (byte)Math.Clamp(db.DbDialect <= 0 ? 3 : db.DbDialect, 1, 3)
        };
        var conn = new FbConnection(csb.ToString());
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureColumnsAsync(conn, cancellationToken).ConfigureAwait(false);
        return conn;
    }

    private async Task EnsureColumnsAsync(FbConnection conn, CancellationToken cancellationToken)
    {
        if (_roleColumnReady) return;
        if (await ColumnMissingAsync(conn, RoleColumn, cancellationToken).ConfigureAwait(false))
            await ExecuteAsync(conn, "ALTER TABLE SY_USER ADD UDF_SYNCROLE VARCHAR(20)", cancellationToken).ConfigureAwait(false);
        if (await ColumnMissingAsync(conn, CreatedColumn, cancellationToken).ConfigureAwait(false))
            await ExecuteAsync(conn, "ALTER TABLE SY_USER ADD UDF_SYNCCREATED TIMESTAMP", cancellationToken).ConfigureAwait(false);
        _roleColumnReady = true;
    }

    private static async Task<bool> ColumnMissingAsync(FbConnection conn, string column, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM RDB$RELATION_FIELDS
            WHERE TRIM(RDB$RELATION_NAME) = 'SY_USER' AND TRIM(RDB$FIELD_NAME) = @Col
            """;
        var count = await ScalarAsync(conn, sql, cancellationToken, ("@Col", column)).ConfigureAwait(false);
        return Convert.ToInt32(count ?? 0) == 0;
    }

    private async Task<string> PasswordAsync(CancellationToken cancellationToken)
    {
        var fromEnv = Environment.GetEnvironmentVariable("FIREBIRD_PASSWORD")
                      ?? Environment.GetEnvironmentVariable("CENTRAL_FB_MASTERKEY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        var resolved = await _secrets.ResolveAsync(_central.PasswordSecretRef, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException("Firebird password is not configured.");
        return resolved.Trim();
    }

    private static async Task<List<RawUser>> ReadAllAsync(FbConnection conn, CancellationToken cancellationToken)
    {
        var sql = $"SELECT {UserColumns} FROM SY_USER ORDER BY AUTOKEY";
        await using var cmd = new FbCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<RawUser>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadRow(reader));
        return rows;
    }

    private static async Task<RawUser?> ReadByIdAsync(FbConnection conn, int id, CancellationToken cancellationToken)
    {
        var sql = $"SELECT FIRST 1 {UserColumns} FROM SY_USER WHERE AUTOKEY = @Id";
        return await ReadSingleAsync(conn, sql, cancellationToken, ("@Id", id)).ConfigureAwait(false);
    }

    private static async Task<RawUser?> ReadByEmailAsync(FbConnection conn, string email, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT FIRST 1 {UserColumns}
            FROM SY_USER
            WHERE UPPER(TRIM(CAST(EMAIL AS VARCHAR(320)))) = UPPER(TRIM(@Email))
            """;
        return await ReadSingleAsync(conn, sql, cancellationToken, ("@Email", email.Trim())).ConfigureAwait(false);
    }

    private static async Task<RawUser?> ReadSingleAsync(
        FbConnection conn, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] args)
    {
        await using var cmd = new FbCommand(sql, conn);
        Bind(cmd, args);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return ReadRow(reader);
    }

    private static RawUser ReadRow(FbDataReader reader) => new(
        Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
        Text(reader, 1),
        Text(reader, 2),
        Text(reader, 3),
        Text(reader, 4),
        reader.IsDBNull(5) || IsActive(reader.GetValue(5)),
        Text(reader, 6),
        reader.IsDBNull(7) ? null : Convert.ToDateTime(reader.GetValue(7), CultureInfo.InvariantCulture));

    private static async Task<bool> EmailExistsAsync(FbConnection conn, string email, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM SY_USER
            WHERE UPPER(TRIM(CAST(EMAIL AS VARCHAR(320)))) = UPPER(TRIM(@Email))
            """;
        var count = await ScalarAsync(conn, sql, cancellationToken, ("@Email", email)).ConfigureAwait(false);
        return Convert.ToInt32(count ?? 0) > 0;
    }

    private static async Task<int> NextIdAsync(FbConnection conn, CancellationToken cancellationToken)
    {
        var next = await ScalarAsync(conn, "SELECT COALESCE(MAX(AUTOKEY), 0) + 1 FROM SY_USER", cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToInt32(next ?? 1, CultureInfo.InvariantCulture);
    }

    private static async Task<string> UniqueCodeAsync(FbConnection conn, string email, CancellationToken cancellationToken)
    {
        var local = email.Split('@')[0];
        var chars = local.Where(char.IsLetterOrDigit).ToArray();
        var baseCode = chars.Length == 0 ? "USER" : new string(chars).ToUpperInvariant();
        if (baseCode.Length > 8) baseCode = baseCode[..8];

        for (var i = 0; i < 100; i++)
        {
            var suffix = i == 0 ? "" : i.ToString(CultureInfo.InvariantCulture);
            var code = baseCode.Length + suffix.Length <= 10 ? baseCode + suffix : (baseCode[..(10 - suffix.Length)] + suffix);
            var taken = await ScalarAsync(
                conn,
                "SELECT COUNT(*) FROM SY_USER WHERE UPPER(TRIM(CODE)) = UPPER(@Code)",
                cancellationToken,
                ("@Code", code)).ConfigureAwait(false);
            if (Convert.ToInt32(taken ?? 0) == 0)
                return code;
        }
        throw new InvalidOperationException("Could not choose a free user code.");
    }

    private static Task InsertAsync(
        FbConnection conn, int id, string code, string name, string mobile, string email, string role, CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO SY_USER (AUTOKEY, CODE, NAME, MOBILE, EMAIL, ISACTIVE, {RoleColumn}, {CreatedColumn})
            VALUES (@Id, @Code, @Name, @Mobile, @Email, TRUE, @Role, CURRENT_TIMESTAMP)
            """;
        return ExecuteAsync(conn, sql, cancellationToken,
            ("@Id", id), ("@Code", code), ("@Name", name), ("@Mobile", mobile), ("@Email", email), ("@Role", role));
    }

    private static Task WriteRoleAsync(FbConnection conn, int id, string role, CancellationToken cancellationToken) =>
        ExecuteAsync(conn, $"UPDATE SY_USER SET {RoleColumn} = @Role WHERE AUTOKEY = @Id", cancellationToken,
            ("@Role", role), ("@Id", id));

    private static async Task<int> CountOtherActiveAdminsAsync(FbConnection conn, int userId, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT COUNT(*) FROM SY_USER
            WHERE AUTOKEY <> @Id
              AND COALESCE(ISACTIVE, TRUE) = TRUE
              AND TRIM(UPPER({RoleColumn})) = @Role
            """;
        var count = await ScalarAsync(conn, sql, cancellationToken, ("@Id", userId), ("@Role", AppRoles.Admin))
            .ConfigureAwait(false);
        return Convert.ToInt32(count ?? 0);
    }

    private static async Task<object?> ScalarAsync(
        FbConnection conn, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] args)
    {
        await using var cmd = new FbCommand(sql, conn);
        Bind(cmd, args);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        FbConnection conn, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] args)
    {
        await using var cmd = new FbCommand(sql, conn);
        Bind(cmd, args);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(FbCommand cmd, (string Name, object Value)[] args)
    {
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
    }

    private static bool IsStoredAdmin(AppUser user) =>
        user.IsActive && AppRoles.Rank(user.Role) >= AppRoles.Rank(AppRoles.Admin);

    private static bool IsKnownRole(string role) =>
        role.Equals(AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
        || role.Equals(AppRoles.Viewer, StringComparison.OrdinalIgnoreCase);

    private static string Text(FbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)?.Trim() ?? "";

    private static bool IsActive(object? value)
    {
        if (value is null || value is DBNull) return true;
        if (value is bool b) return b;
        if (value is short s) return s != 0;
        if (value is int i) return i != 0;
        var t = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return t is null or "" or "1" or "T" or "Y" or "TRUE";
    }

    private sealed record RawUser(int Id, string Code, string Name, string Mobile, string Email, bool Active, string Role, DateTime? Created);
}
