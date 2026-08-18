using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;

namespace SqlAccountSync.Pages.Audit;

public class IndexModel : SyncPageModel
{
    private readonly IAuditService _audit;
    public IndexModel(IAuditService audit) => _audit = audit;
    public List<AuditEntry> Items { get; set; } = [];
    public async Task OnGetAsync() => Items = await _audit.ListAsync();
}
