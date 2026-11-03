namespace SqlAccountSync.Services.Sdk;

public sealed class SdkCompanyCredentials
{
    public string Name { get; set; } = "";
    public string DcfPath { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "ADMIN";
    public string Password { get; set; } = "";
}

public sealed class SdkHealthStatus
{
    public bool ProgIdRegistered { get; set; }
    public bool LoggedIn { get; set; }
    public string CurrentCompany { get; set; } = "";
    public bool SessionOwnedByUs { get; set; }
    public string LastError { get; set; } = "";
    public string Guidance { get; set; } = "";
    public string LocalServer32 { get; set; } = "";
}
