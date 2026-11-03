using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAccountSync.Authorization;
using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Controllers;

/// <summary>
/// Maintain Account (chart of accounts).
/// GET  /api/accounts?dbCode=HQ           — full chart from Firebird GL_ACC (default)
/// GET  /api/accounts?dbCode=HQ&amp;source=api — proxy SQL Acc GET /account/* (paid REST; blocked when Provider=Sdk)
/// POST /api/accounts?dbCode=HQ           — create via <see cref="ISqlAccountWriteAdapter"/> (Rest or Sdk)
/// </summary>
[ApiController]
[Route("api/accounts")]
[Authorize(Policy = PolicyNames.Viewer)]
public sealed class AccountsController : ControllerBase
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountApiClient _api;
    private readonly ISqlAccountReadAdapter _read;
    private readonly ISqlAccountWriteAdapter _write;
    private readonly SqlAccountingWriteOptions _writeOptions;

    public AccountsController(
        IDatabaseRegistry registry,
        ISqlAccountApiClient api,
        ISqlAccountReadAdapter read,
        ISqlAccountWriteAdapter write,
        IOptions<SqlAccountingWriteOptions> writeOptions)
    {
        _registry = registry;
        _api = api;
        _read = read;
        _write = write;
        _writeOptions = writeOptions.Value;
    }

    /// <summary>List full GL chart for a registered database.</summary>
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] int? dbId,
        [FromQuery] string? dbCode,
        [FromQuery] string? source,
        CancellationToken cancellationToken)
    {
        var db = await ResolveDbAsync(dbId, dbCode, cancellationToken).ConfigureAwait(false);
        if (db is null)
            return NotFound(new { error = "Database not found. Pass dbId or dbCode." });

        var useApi = string.Equals(source, "api", StringComparison.OrdinalIgnoreCase);
        if (useApi)
        {
            if (string.Equals(
                    _writeOptions.Provider?.Trim(),
                    SqlAccountingWriteProviders.Sdk,
                    StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    error = "source=api is disabled when SqlAccountingWrite:Provider=Sdk. Use Firebird chart (omit source) via GetGlChartAsync."
                });
            }

            return await ListFromSqlApiAsync(db, cancellationToken).ConfigureAwait(false);
        }

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

    /// <summary>Create a GL account through the configured write adapter (REST default; SDK when Provider=Sdk).</summary>
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
                ["dockey"] = body.DocKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["parent"] = body.Parent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [GlAccountMapper.FieldDestParent] = body.Parent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [GlAccountMapper.FieldDescription2] = (body.Description2 ?? "").Trim(),
                [GlAccountMapper.FieldAccType] = (body.AccType ?? "").Trim(),
                [GlAccountMapper.FieldSpecialAccType] = (body.SpecialAccType ?? "").Trim(),
                [GlAccountMapper.FieldTax] = (body.Tax ?? "").Trim(),
                [GlAccountMapper.FieldCashFlowType] = body.CashFlowType.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [GlAccountMapper.FieldSic] = (body.Sic ?? "").Trim()
            }
        };

        var write = await _write.CreateMasterAsync(
            db, EntityCodes.Account, record, $"API-CREATE-{record.Code}", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!write.Success)
            return StatusCode(502, new { error = write.Message ?? "Create failed." });

        return Ok(new
        {
            ok = true,
            dbId = db.DbId,
            dbCode = db.DbCode,
            code = record.Code,
            message = write.Message
        });
    }

    private async Task<IActionResult> ListFromSqlApiAsync(Models.RegisteredDatabase db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(db.ApiBaseUrl) || string.IsNullOrWhiteSpace(db.ApiCredRef))
            return BadRequest(new { error = $"{db.DisplayName} has no SQL Accounting API credentials." });

        var response = await _api.GetAsync(db, "/account/*", cancellationToken).ConfigureAwait(false);
        if (!response.Available)
            return StatusCode(502, new { error = "SQL Accounting API credentials could not be resolved." });

        if (!response.IsSuccess)
        {
            return StatusCode(response.Status > 0 ? response.Status : 502, new
            {
                error = $"SQL Accounting API returned {response.Status}.",
                detail = SqlAccountApiClient.SummarizeApiError(response.Body)
            });
        }

        List<GlAccountRow> accounts;
        try
        {
            accounts = GlAccountTree.ParseAndOrder(response.Body);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = "Could not parse account list.", detail = ex.Message });
        }

        return Ok(new
        {
            dbId = db.DbId,
            dbCode = db.DbCode,
            displayName = db.DisplayName,
            source = "api",
            count = accounts.Count,
            data = accounts
        });
    }

    private async Task<Models.RegisteredDatabase?> ResolveDbAsync(
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
