using SqlAccountSync.Models;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.Queue;

namespace SqlAccountSync.Pages;

public class IndexModel : SyncPageModel
{
    private readonly IDatabaseRegistry _registry;
    private readonly IJobQueue _jobs;

    public IndexModel(IDatabaseRegistry registry, IJobQueue jobs)
    {
        _registry = registry;
        _jobs = jobs;
    }

    public DashboardStats Stats { get; set; } = new();
    public List<RegisteredDatabase> Databases { get; set; } = [];
    public List<DatabaseLink> Links { get; set; } = [];
    public List<SyncJob> RecentJobs { get; set; } = [];

    public async Task OnGetAsync()
    {
        Stats = await _registry.DashboardAsync();
        Databases = await _registry.ListAsync();
        Links = await _registry.ListLinksAsync();
        RecentJobs = await _jobs.ListAsync(take: 8);
    }
}
