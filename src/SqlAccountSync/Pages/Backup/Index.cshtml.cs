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

    [BindProperty]
    public string TargetId { get; set; } = CentralDbBackupTargets.DefaultId;

    public bool BackupReady { get; private set; }
    public string? SourcePath { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }
    public IReadOnlyList<CentralDbBackupTarget> Targets { get; private set; } = [];

    public void OnGet()
    {
        LoadPage();
        if (TempData["BackupOk"] is string ok && !string.IsNullOrWhiteSpace(ok))
            SuccessMessage = ok;
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        if (!CurrentUser.IsAdmin(User))
            return Forbid();

        LoadPage();
        if (!BackupReady)
        {
            ErrorMessage = "CentralDb:Path is missing or the SYNC.FDB file was not found.";
            return Page();
        }

        try
        {
            var result = await _backup.SaveZipAsync(TargetId, cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(new AuditEntry
            {
                UserEmail = CurrentUser.Email(User),
                Action = "BACKUP_CENTRAL_DB",
                Entity = "CENTRAL",
                RecordId = result.FileName,
                Result = "SUCCESS",
                AfterJson =
                    $"{{\"bytes\":{result.ByteLength},\"zip\":\"{EscapeJson(result.SavedZipPath)}\",\"source\":\"{EscapeJson(result.SourcePath)}\"}}"
            }, cancellationToken).ConfigureAwait(false);

            TempData["BackupOk"] = $"Saved {result.FileName} ({FormatBytes(result.ByteLength)}) to {result.SavedZipPath}";
            return RedirectToPage(new { targetId = TargetId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Central SYNC.FDB backup failed");
            ErrorMessage = "Backup failed: " + ex.Message;
            return Page();
        }
    }

    private void LoadPage()
    {
        BackupReady = _backup.IsConfigured;
        SourcePath = _backup.ConfiguredPath;
        Targets = _backup.ListTargets();
        if (string.IsNullOrWhiteSpace(TargetId))
            TargetId = Request.Query["targetId"].FirstOrDefault() ?? CentralDbBackupTargets.DefaultId;
        if (CentralDbBackupTargets.ResolvePath(TargetId, SourcePath) is null)
            TargetId = CentralDbBackupTargets.DefaultId;
        if (!BackupReady && string.IsNullOrWhiteSpace(ErrorMessage))
            ErrorMessage = "CentralDb:Path is missing or the SYNC.FDB file was not found.";
    }

    private static string EscapeJson(string value) =>
        (value ?? "").Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.#") + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
    }
}
