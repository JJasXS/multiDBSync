namespace SqlAccountSync.Services.Tenants;

/// <summary>Menu visibility loaded from the AWS tenant record. Missing flags stay visible.</summary>
public sealed class TenantMenuFlags
{
    private int _sync = 1;
    private int _docs = 1;

    public bool ShowSynchronization => _sync != 0;
    public bool ShowDocumentTransfer => _docs != 0;

    public void Apply(bool showSynchronization, bool showDocumentTransfer)
    {
        _sync = showSynchronization ? 1 : 0;
        _docs = showDocumentTransfer ? 1 : 0;
    }
}
