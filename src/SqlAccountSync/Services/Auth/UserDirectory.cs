using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlAccountSync.Authorization;
using SqlAccountSync.Data;
using SqlAccountSync.Models;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.Auth;

public interface IUserDirectory
{
    Task<AppUser?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<List<AppUser>> ListAsync(CancellationToken cancellationToken = default);
    Task<int> SaveAsync(AppUser user, CancellationToken cancellationToken = default);
    Task DeactivateAsync(int userId, CancellationToken cancellationToken = default);
    Task<bool> CanAccessDbAsync(int userId, string role, int dbId, bool forSync, CancellationToken cancellationToken = default);
    Task SetAclAsync(int userId, int dbId, bool canView, bool canSync, CancellationToken cancellationToken = default);
    Task<List<UserDbAcl>> ListAclAsync(int userId, CancellationToken cancellationToken = default);
}

public sealed class UserDirectory : IUserDirectory
{
    private readonly CentralDb _db;
    private readonly IHostEnvironment _env;
    private readonly BootstrapOptions _bootstrap;

    public UserDirectory(CentralDb db, IHostEnvironment env, IOptions<BootstrapOptions> bootstrap)
    {
        _db = db;
        _env = env;
        _bootstrap = bootstrap.Value;
    }

    public Task<AppUser?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
            return Task.FromResult(LocalDevUser(email));
        return _db.QuerySingleAsync(
            "SELECT FIRST 1 * FROM SYNC_USER WHERE UPPER(EMAIL)=UPPER(@E) AND ISACTIVE=1",
            Map, new { E = email.Trim() }, cancellationToken);
    }

    public Task<List<AppUser>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!_db.IsConfigured)
        {
            var local = LocalDevUser(_bootstrap.AdminEmail);
            return Task.FromResult(local is null ? new List<AppUser>() : new List<AppUser> { local });
        }
        return _db.QueryAsync("SELECT * FROM SYNC_USER ORDER BY DISPLAYNAME", Map, cancellationToken: cancellationToken);
    }

    public async Task<int> SaveAsync(AppUser user, CancellationToken cancellationToken = default)
    {
        if (user.UserId == 0)
        {
            var id = (int)await _db.NextIdAsync("GEN_SYNC_USER", cancellationToken).ConfigureAwait(false);
            await _db.ExecuteAsync(@"
INSERT INTO SYNC_USER (USERID, EMAIL, DISPLAYNAME, USERROLE, ISACTIVE, CREATEDAT)
VALUES (@USERID, @EMAIL, @DISPLAYNAME, @USERROLE, 1, CURRENT_TIMESTAMP)",
                new { USERID = id, EMAIL = user.Email.Trim().ToLowerInvariant(), DISPLAYNAME = user.DisplayName.Trim(), USERROLE = user.Role.Trim().ToUpperInvariant() },
                cancellationToken).ConfigureAwait(false);
            return id;
        }

        await _db.ExecuteAsync(@"
UPDATE SYNC_USER SET EMAIL=@EMAIL, DISPLAYNAME=@DISPLAYNAME, USERROLE=@USERROLE, ISACTIVE=@ISACTIVE
WHERE USERID=@USERID",
            new
            {
                USERID = user.UserId,
                EMAIL = user.Email.Trim().ToLowerInvariant(),
                DISPLAYNAME = user.DisplayName.Trim(),
                USERROLE = user.Role.Trim().ToUpperInvariant(),
                ISACTIVE = user.IsActive ? 1 : 0
            }, cancellationToken).ConfigureAwait(false);
        return user.UserId;
    }

    public Task DeactivateAsync(int userId, CancellationToken cancellationToken = default) =>
        _db.ExecuteAsync("UPDATE SYNC_USER SET ISACTIVE=0 WHERE USERID=@USERID", new { USERID = userId }, cancellationToken);

    public async Task<bool> CanAccessDbAsync(int userId, string role, int dbId, bool forSync, CancellationToken cancellationToken = default)
    {
        if (AppRoles.Rank(role) >= AppRoles.Rank(AppRoles.Admin))
            return true;
        var acl = await _db.QuerySingleAsync(
            "SELECT FIRST 1 * FROM SYNC_USER_DBACL WHERE USERID=@USERID AND DBID=@DBID",
            r => new UserDbAcl
            {
                CanView = CentralDb.Flag(r, "CANVIEW"),
                CanSync = CentralDb.Flag(r, "CANSYNC")
            }, new { USERID = userId, DBID = dbId }, cancellationToken).ConfigureAwait(false);
        if (acl is null)
            return AppRoles.Rank(role) >= AppRoles.Rank(AppRoles.Viewer) && !forSync && AppRoles.Rank(role) >= AppRoles.Rank(AppRoles.Manager);
        return forSync ? acl.CanSync : acl.CanView;
    }

    public async Task SetAclAsync(int userId, int dbId, bool canView, bool canSync, CancellationToken cancellationToken = default)
    {
        var existing = await _db.QuerySingleAsync(
            "SELECT FIRST 1 ACLID FROM SYNC_USER_DBACL WHERE USERID=@USERID AND DBID=@DBID",
            r => CentralDb.Int(r, "ACLID"), new { USERID = userId, DBID = dbId }, cancellationToken).ConfigureAwait(false);
        if (existing is 0)
        {
            var id = (int)await _db.NextIdAsync("GEN_SYNC_ACL", cancellationToken).ConfigureAwait(false);
            await _db.ExecuteAsync(@"
INSERT INTO SYNC_USER_DBACL (ACLID, USERID, DBID, CANVIEW, CANSYNC)
VALUES (@ACLID, @USERID, @DBID, @CANVIEW, @CANSYNC)",
                new { ACLID = id, USERID = userId, DBID = dbId, CANVIEW = canView ? 1 : 0, CANSYNC = canSync ? 1 : 0 }, cancellationToken);
            return;
        }

        await _db.ExecuteAsync("UPDATE SYNC_USER_DBACL SET CANVIEW=@CANVIEW, CANSYNC=@CANSYNC WHERE ACLID=@ACLID",
            new { ACLID = existing, CANVIEW = canView ? 1 : 0, CANSYNC = canSync ? 1 : 0 }, cancellationToken);
    }

    public Task<List<UserDbAcl>> ListAclAsync(int userId, CancellationToken cancellationToken = default) =>
        _db.QueryAsync("SELECT * FROM SYNC_USER_DBACL WHERE USERID=@USERID", r => new UserDbAcl
        {
            AclId = CentralDb.Int(r, "ACLID"),
            UserId = CentralDb.Int(r, "USERID"),
            DbId = CentralDb.Int(r, "DBID"),
            CanView = CentralDb.Flag(r, "CANVIEW"),
            CanSync = CentralDb.Flag(r, "CANSYNC")
        }, new { USERID = userId }, cancellationToken);

    private AppUser? LocalDevUser(string? email)
    {
        if (!_env.IsDevelopment())
            return null;
        var expected = (_bootstrap.AdminEmail ?? "admin@localhost").Trim();
        if (string.IsNullOrWhiteSpace(email) || !string.Equals(email.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            return null;
        return new AppUser
        {
            UserId = 1,
            Email = expected.ToLowerInvariant(),
            DisplayName = string.IsNullOrWhiteSpace(_bootstrap.AdminName) ? "Administrator" : _bootstrap.AdminName,
            Role = AppRoles.Admin,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static AppUser Map(FirebirdSql.Data.FirebirdClient.FbDataReader r) => new()
    {
        UserId = CentralDb.Int(r, "USERID"),
        Email = CentralDb.Str(r, "EMAIL"),
        DisplayName = CentralDb.Str(r, "DISPLAYNAME"),
        Role = CentralDb.Str(r, "USERROLE"),
        IsActive = CentralDb.Flag(r, "ISACTIVE"),
        CreatedAt = CentralDb.TimeReq(r, "CREATEDAT")
    };
}
