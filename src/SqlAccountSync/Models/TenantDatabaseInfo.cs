namespace SqlAccountSync.Models;

/// <summary>Firebird connection fields from tenant-config API <c>database</c> block (login DB for SY_USER).</summary>
public sealed class TenantDatabaseInfo
{
    public string DbHost { get; set; } = "localhost";
    public int DbPort { get; set; } = 3050;
    public string DbPath { get; set; } = "";
    public string DbCharset { get; set; } = "UTF8";
    public int DbDialect { get; set; } = 3;
}
