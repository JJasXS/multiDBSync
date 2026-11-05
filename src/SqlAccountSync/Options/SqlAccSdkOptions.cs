namespace SqlAccountSync.Options;

/// <summary>Write-path provider. REST remains default until SDK testing succeeds.</summary>
public static class SqlAccountingWriteProviders
{
    public const string Rest = "Rest";
    public const string Sdk = "Sdk";

    public static bool IsSdk(string? provider) =>
        string.Equals((provider ?? "").Trim(), Sdk, StringComparison.OrdinalIgnoreCase);

    public static bool IsRest(string? provider) => !IsSdk(provider);
}

public sealed class SqlAccountingWriteOptions
{
    public const string SectionName = "SqlAccountingWrite";

    /// <summary><see cref="SqlAccountingWriteProviders.Sdk"/> (this branch default) or <see cref="SqlAccountingWriteProviders.Rest"/>.</summary>
    public string Provider { get; set; } = SqlAccountingWriteProviders.Sdk;

    /// <summary>
    /// When true, SQL Accounting REST HTTP is refused even before SigV4 (SDK-only labs).
    /// Keep false in Production so the nav API|SDK toggle can still use REST when selected.
    /// </summary>
    public bool BlockRestHttp { get; set; }

    /// <summary>True when <see cref="BlockRestHttp"/> is set (explicit lab lock).</summary>
    public bool ShouldRefuseRestHttp() => BlockRestHttp;
}

/// <summary>
/// SQL Accounting SDK Live (COM) settings. Live COM is off by default so automated tests
/// never launch SQLACC.exe or touch company databases.
/// </summary>
public sealed class SqlAccSdkOptions
{
    public const string SectionName = "SqlAccSdk";

    public string ProgId { get; set; } = "SQLAcc.BizApp";

    /// <summary>
    /// When false, the SDK write adapter refuses live COM and returns a clear error.
    /// Enable only for intentional local Login/Write experiments.
    /// </summary>
    public bool AllowLiveCom { get; set; }

    /// <summary>
    /// Drive auto-push on a single SQLACC.exe: Logout → Login → write → next company on the same
    /// COM instance. When true (default), if an existing SQL Account cannot be attached via COM,
    /// leftover SQLACC.exe processes are closed before CreateInstance so a second tab is not opened.
    /// </summary>
    public bool PreferRunningInstance { get; set; } = true;

    /// <summary>
    /// After each SDK write/use finishes, call Logout so the concurrent user seat is freed
    /// (1-user licenses cannot open SQL Account while sync stays logged in).
    /// When attached to a user window, Logout still runs; the window is left at the login screen.
    /// </summary>
    public bool LogoutAfterUse { get; set; } = true;

    /// <summary>
    /// After Logout, dispose the owned COM LocalServer and close leftover SQLACC.exe.
    /// Skipped when attached to a user-owned SQL Account window (window is kept, logged out).
    /// </summary>
    public bool ReleaseSqlAccAfterUse { get; set; } = true;

    /// <summary>Default DCF full path used when a registered database does not override it.</summary>
    public string DefaultDcfPath { get; set; } = "";

    /// <summary>SQL Accounting application user name for COM Login (not Firebird SYSDBA).</summary>
    public string UserName { get; set; } = "";

    /// <summary>Secret ref for the SQL Accounting application password (e.g. env:… / sm:…).</summary>
    public string PasswordSecretRef { get; set; } = "";

    /// <summary>Explicit DCF path for Read POC Login (never omit / never use last-used).</summary>
    public string ExplicitDcfPath { get; set; } = "";

    /// <summary>Explicit FDB file name for Read POC Login (file name only).</summary>
    public string ExplicitDatabaseFileName { get; set; } = "";

    /// <summary>
    /// Human authorization to allow a LAN/UNC Read POC. Still requires
    /// <see cref="AuthorizedExactTarget"/> exact match. Default false.
    /// </summary>
    public bool AuthorizeLanRead { get; set; }

    /// <summary>
    /// Exact authorized target as <c>DCF|FDB</c>, or semicolon-separated allow-list
    /// (e.g. Miracle and Onyat). Normalized path separators.
    /// </summary>
    public string AuthorizedExactTarget { get; set; } = "";
}
