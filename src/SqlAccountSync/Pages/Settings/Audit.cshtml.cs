using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;

namespace SqlAccountSync.Pages.Settings;

public class AuditModel : SyncPageModel
{
    private readonly IAuditService _audit;

    public AuditModel(IAuditService audit) => _audit = audit;

    public List<AuditListItem> Audits { get; set; } = [];

    public async Task OnGetAsync() => Audits = await _audit.ListDetailedAsync();
}
