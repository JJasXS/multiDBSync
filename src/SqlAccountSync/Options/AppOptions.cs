namespace SqlAccountSync.Options;

public sealed class CentralDbOptions
{
    public const string SectionName = "CentralDb";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3050;
    public string Path { get; set; } = "";
    public string User { get; set; } = "SYSDBA";
    public string Charset { get; set; } = "UTF8";
    public int Dialect { get; set; } = 3;
    public string PasswordSecretRef { get; set; } = "env:CENTRAL_FB_MASTERKEY";
    public bool Pooling { get; set; } = true;
}

/// <summary>
/// Dashboard company registry source. <see cref="Mode"/> Local reads <see cref="CentralDbOptions"/>.
/// Live reads another Firebird SYNC database with SELECT only. Writes always stay on <see cref="CentralDbOptions"/>.
/// </summary>
public sealed class CentralRegistryOptions
{
    public const string SectionName = "CentralRegistry";
    public const string LocalMode = "Local";
    public const string LiveMode = "Live";

    public string Mode { get; set; } = LocalMode;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3050;
    public string Path { get; set; } = "";
    public string User { get; set; } = "SYSDBA";
    public string Charset { get; set; } = "UTF8";
    public int Dialect { get; set; } = 3;
    public string PasswordSecretRef { get; set; } = "env:CENTRAL_FB_MASTERKEY";

    public bool IsLiveRequested =>
        string.Equals(Mode?.Trim(), LiveMode, StringComparison.OrdinalIgnoreCase);

    public bool HasLiveTarget =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Path);
}

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string PasswordSecretRef { get; set; } = "env:SMTP_PASSWORD";
    public string From { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
}

public sealed class SyncOptions
{
    public const string SectionName = "Sync";
    public int SessionHours { get; set; } = 8;
    public int OtpMinutes { get; set; } = 10;
    public int WorkerPollSeconds { get; set; } = 5;
    /// <summary>
    /// How often to auto-push missing ACCOUNT codes from MASTER/BOTH to linked subs.
    /// Set 0 to disable. Default 60 (one minute).
    /// </summary>
    public int AccountAutoPushSeconds { get; set; } = 60;
    /// <summary>
    /// Cancel PENDING jobs that have never started after this many seconds.
    /// Set 0 to disable. Default 120 (2 minutes).
    /// </summary>
    public int MaxQueueWaitSeconds { get; set; } = 120;
    /// <summary>
    /// When true, auto-push also PUTs customer/supplier/account rows whose syncable fields differ on the sub
    /// (for entities whose Push checkbox is on).
    /// </summary>
    public bool AccountAutoUpdateEnabled { get; set; } = true;
    /// <summary>
    /// When true, auto-push DELETEs mapped customer/supplier/account rows on the sub that no longer
    /// exist on master (for entities whose Push checkbox is on).
    /// </summary>
    public bool AccountAutoDeleteEnabled { get; set; } = true;
    /// <summary>Max delete jobs enqueued per master→sub pair per cycle.</summary>
    public int AccountAutoDeleteMaxPerCycle { get; set; } = 25;
    public int MaxAttempts { get; set; } = 5;
    /// <summary>
    /// Max seconds for one SDK COM call (login/save). On timeout BizApp is recreated.
    /// Default 60. Clamped to 5–600 at runtime.
    /// </summary>
    public int SdkCallTimeoutSeconds { get; set; } = 60;
    /// <summary>
    /// Seconds a job may stay PROCESSING before the worker reclaims it as RETRYING (crash recovery).
    /// Default 900 (15 minutes). Clamped to 60–86400 at runtime.
    /// </summary>
    public int ProcessingLeaseSeconds { get; set; } = 900;
    public bool AllowProductionConnections { get; set; }
    public bool AllowCrossEnvironment { get; set; }
    /// <summary>
    /// <c>dev</c> = developer testing phase. <c>prod</c> = email OTP only (default).
    /// Debug OTP reveal also requires <see cref="AllowLocalOtpReveal"/>, Development env, and loopback client.
    /// </summary>
    public string AppMode { get; set; } = "prod";
    /// <summary>Obsolete: ignored. Kept so old config keys do not break binding.</summary>
    public bool DebugOtpToConsole { get; set; }
    /// <summary>Obsolete: ignored. Fixed OTPs are no longer supported.</summary>
    public string MockSignInCode { get; set; } = "";
    /// <summary>
    /// Explicit opt-in to return OTP in the login JSON response. Still requires Development + AppMode=dev
    /// and a loopback client IP. Default false — never enable on publicly reachable hosts.
    /// </summary>
    public bool AllowLocalOtpReveal { get; set; }
    /// <summary>
    /// Secret ref (<c>env:</c> / <c>sm:</c>) for HMAC pepper used to hash OTPs. Required for multi-instance.
    /// </summary>
    public string OtpHashPepperSecretRef { get; set; } = "";

    public bool IsDevAppMode =>
        string.Equals((AppMode ?? "").Trim(), "dev", StringComparison.OrdinalIgnoreCase);
    /// <summary>Tenant code used for SDK Secrets Manager path (e.g. TNT10006).</summary>
    public string TenantCode { get; set; } = "";
    /// <summary>
    /// Optional override for the SDK connections secret id.
    /// Default is <c>proacc/tenant/{TenantCode}/sdkConnections</c>. Env <c>SDK_SECRET_ID</c> also works.
    /// </summary>
    public string SdkSecretId { get; set; } = "";
    /// <summary>
    /// Company DbCode to leave the SDK logged into when the job queue is idle (so SQL Account stays on Master).
    /// Empty = first active MASTER/BOTH database that has SDK credentials.
    /// </summary>
    public string SdkHomeDbCode { get; set; } = "";
    public string[] RequiredTables { get; set; } = ["ST_ITEM", "AR_CUSTOMER", "SY_USER"];
}

public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";
    public string AdminEmail { get; set; } = "admin@localhost";
    public string AdminName { get; set; } = "Administrator";
}

public sealed class AwsOptions
{
    public const string SectionName = "AWS";
    public string Region { get; set; } = "ap-southeast-1";
}

public sealed class FeatureOptions
{
    public const string SectionName = "Features";
    /// <summary>When false, Mappings nav/links are hidden and /Mappings redirects away. Backend mapping still runs.</summary>
    public bool ShowMappingsUi { get; set; }
    /// <summary>When false, Sync configuration nav/links are hidden and /SyncConfig redirects away. Rules still apply if configured.</summary>
    public bool ShowSyncConfigUi { get; set; }
}
