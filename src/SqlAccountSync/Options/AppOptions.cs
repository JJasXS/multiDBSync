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
    /// <summary>When true, auto-push also PUTs accounts whose syncable fields differ on the sub.</summary>
    public bool AccountAutoUpdateEnabled { get; set; } = true;
    /// <summary>
    /// When true, auto-push DELETEs accounts on the sub that we previously created
    /// (SYNC_MAPPING with SubKey set) and that no longer exist on master.
    /// </summary>
    public bool AccountAutoDeleteEnabled { get; set; } = true;
    /// <summary>Max delete jobs enqueued per master→sub pair per cycle.</summary>
    public int AccountAutoDeleteMaxPerCycle { get; set; } = 25;
    public int MaxAttempts { get; set; } = 5;
    public bool AllowProductionConnections { get; set; }
    public bool AllowCrossEnvironment { get; set; }
    public bool DebugOtpToConsole { get; set; }
    /// <summary>Fixed OTP used only when DebugOtpToConsole is true (Development).</summary>
    public string MockSignInCode { get; set; } = "";
    /// <summary>Tenant code used for standardized SQL API secret paths (e.g. TNT10006).</summary>
    public string TenantCode { get; set; } = "";
    /// <summary>
    /// When true and <see cref="TenantCode"/> is set, blank/convention ApiCredRef is filled as
    /// <c>sm:proacc/tenant/{TenantCode}/sqlApiCredentials/{DbCode}</c> after save
    /// (falls back to numeric DBID if Code is empty).
    /// </summary>
    public bool AutoAssignSqlApiCredRefByDbId { get; set; } = true;
    /// <summary>When true, create an empty Secrets Manager placeholder for the assigned path if missing.</summary>
    public bool EnsureSqlApiSecretPlaceholder { get; set; } = true;
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
