namespace SqlAccountSync.Options;

/// <summary>
/// Development opt-in: Logs page SELECT-only reads against a remote SYNC.FDB (TEST).
/// Disabled by default. Does not change <see cref="CentralDbOptions"/> used by workers/writes.
/// </summary>
public sealed class LogsRemoteReadOptions
{
    public const string SectionName = "LogsRemoteRead";

    /// <summary>When false (default), Logs uses the local <see cref="CentralDbOptions"/> connection.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true (default), reuse <see cref="CentralRegistryOptions"/> Live Host/Path/User/PasswordSecretRef.
    /// When false, use the Host/Path fields below.
    /// </summary>
    public bool UseCentralRegistry { get; set; } = true;

    public string Host { get; set; } = "";
    public int Port { get; set; } = 3050;
    public string Path { get; set; } = "";
    public string User { get; set; } = "SYSDBA";
    public string Charset { get; set; } = "UTF8";
    public int Dialect { get; set; } = 3;
    public string PasswordSecretRef { get; set; } = "env:CENTRAL_FB_MASTERKEY";
}
