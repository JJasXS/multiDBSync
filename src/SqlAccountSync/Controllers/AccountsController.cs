using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Authorization;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Controllers;

/// <summary>
/// Maintain Account (chart of accounts).
/// GET  /api/accounts?dbCode=HQ — full chart from Firebird GL_ACC
/// POST /api/accounts?dbCode=HQ — create via SQL Account SDK
/// </summary>
[ApiController]
[Route("api/accounts")]
[Authorize(Policy = PolicyNames.Viewer)]
public sealed class AccountsController : ControllerBase
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountWriteAdapter _write;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ILogger<AccountsController> _logger;

    public AccountsController(
        IDatabaseRegistry registry,
        ISqlAccountWriteAdapter write,
        ISqlAccountReadAdapter read,
        ILogger<AccountsController> logger)
    {
        _registry = registry;
        _write = write;
        _read = read;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] int? dbId,
        [FromQuery] string? dbCode,
        CancellationToken cancellationToken)
    {
        var db = await ResolveDbAsync(dbId, dbCode, cancellationToken).ConfigureAwait(false);
        if (db is null)
            return NotFound(new { error = "Database not found. Pass dbId or dbCode." });

        var accounts = await _read.GetGlChartAsync(db, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            dbId = db.DbId,
            dbCode = db.DbCode,
            displayName = db.DisplayName,
            source = "firebird",
            count = accounts.Count,
            data = accounts
        });
    }

    [HttpPost]
    [Authorize(Policy = PolicyNames.Operator)]
    public async Task<IActionResult> CreateAsync(
        [FromQuery] int? dbId,
        [FromQuery] string? dbCode,
        [FromBody] CreateGlAccountRequest body,
        CancellationToken cancellationToken)
    {
        var db = await ResolveDbAsync(dbId, dbCode, cancellationToken).ConfigureAwait(false);
        if (db is null)
            return NotFound(new { error = "Database not found. Pass dbId or dbCode." });

        if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Description))
            return BadRequest(new { error = "code and description are required." });

        var record = new MasterRecord
        {
            Code = body.Code.Trim(),
            Description = body.Description.Trim(),
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dockey"] = body.DocKey.ToString(),
                ["parent"] = body.Parent.ToString(),
                [GlAccountMapper.FieldDestParent] = body.Parent.ToString(),
                [GlAccountMapper.FieldDescription2] = body.Description2 ?? "",
                [GlAccountMapper.FieldAccType] = body.AccType ?? "",
                [GlAccountMapper.FieldSpecialAccType] = body.SpecialAccType ?? "",
                [GlAccountMapper.FieldTax] = body.Tax ?? "",
                [GlAccountMapper.FieldCashFlowType] = body.CashFlowType.ToString(),
                [GlAccountMapper.FieldSic] = body.Sic ?? ""
            }
        };

        var write = await _write.CreateMasterAsync(
            db, EntityCodes.Account, record, "API|" + DateTime.UtcNow.ToString("o"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!write.Success)
        {
            var rid = RequestId.Current(HttpContext);
            _logger.LogWarning(
                "Account create failed db={DbCode} requestId={RequestId}: {Detail}",
                db.DbCode, rid, CredentialRedactor.Redact(write.Message));
            return StatusCode(502, new { error = "Create failed.", requestId = rid });
        }

        return Ok(new
        {
            ok = true,
            dbId = db.DbId,
            dbCode = db.DbCode,
            code = body.Code.Trim(),
            requestId = RequestId.Current(HttpContext)
        });
    }

    private async Task<RegisteredDatabase?> ResolveDbAsync(
        int? dbId, string? dbCode, CancellationToken cancellationToken)
    {
        if (dbId is > 0)
            return await _registry.GetAsync(dbId.Value, cancellationToken).ConfigureAwait(false);

        var code = (dbCode ?? "").Trim();
        if (code.Length == 0)
            return null;

        var all = await _registry.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(d =>
            string.Equals(d.DbCode, code, StringComparison.OrdinalIgnoreCase));
    }
}
