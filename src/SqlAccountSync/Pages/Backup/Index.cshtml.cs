using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SqlAccountSync.Authorization;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Audit;
using SqlAccountSync.Services.Auth;
using SqlAccountSync.Services.Backup;

namespace SqlAccountSync.Pages.Backup;

[Authorize(Policy = PolicyNames.Admin)]
public class IndexModel : PageModel
{
    private readonly ICentralDbBackupService _backup;
    private readonly IAuditService _audit;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ICentralDbBackupService backup, IAuditService audit, ILogger<IndexModel> logger)
    {
        _backup = backup;
        _audit = audit;
        _logger = logger;
    }

    public bool BackupReady { get; private set; }
    public string? SourcePath { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
        BackupReady = _backup.IsConfigured;
        SourcePath = _backup.ConfiguredPath;
        if (!BackupReady)
            ErrorMessage = "CentralDb:Path is missing or the SYNC.FDB file was not found.";
    }

    public async Task<IActionResult> OnPostDownloadAsync(CancellationToken cancellationToken)
    {
        if (!CurrentUser.IsAdmin(User))
            return Forbid();

        try
        {
            var result = await _backup.CreateZipAsync(cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(new AuditEntry
            {
                UserEmail = CurrentUser.Email(User),
                Action = "BACKUP_CENTRAL_DB",
                Entity = "CENTRAL",
                RecordId = result.DownloadFileName,
                Result = "SUCCESS",
                AfterJson = $"{{\"bytes\":{result.ZipBytes.Length},\"source\":\"{result.SourcePath.Replace("\\", "\\\\")}\"}}"
            }, cancellationToken).ConfigureAwait(false);

            return File(result.ZipBytes, "application/zip", result.DownloadFileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Central SYNC.FDB backup failed");
            TempData["Flash"] = "Backup failed: " + ex.Message;
            return RedirectToPage();
        }
    }
}
