namespace SqlAccountSync.Models;

/// <summary>SMTP overrides from tenant API: <c>email</c> base + <c>proaccEmail</c> overlay (eQuotation / ApprovalPO parity).</summary>
public sealed class TenantEmailOverride
{
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpSenderEmail { get; set; }
    public string? SmtpAppPasswordSecretRef { get; set; }
    public string? SmtpCredentialsSecretRef { get; set; }
    public bool? OtpEnabled { get; set; }
}
