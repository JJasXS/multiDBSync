using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Authorization;
using SqlAccountSync.Services.Accounts;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount;

namespace SqlAccountSync.Controllers;

/// <summary>
/// Maintain Account (chart of accounts).
/// GET  /api/accounts?dbCode=HQ           — full chart from Firebird GL_ACC (default)
/// GET  /api/accounts?dbCode=HQ&amp;source=api — proxy SQL Acc GET /account/*
/// POST /api/accounts?dbCode=HQ           — create via SQL Acc POST /account
/// </summary>
[ApiController]
[Route("api/accounts")]
[Authorize(Policy = PolicyNames.Viewer)]
public sealed class AccountsController : ControllerBase
{
    private readonly IDatabaseRegistry _registry;
    private readonly ISqlAccountApiClient _api;
    private readonly ISqlAccountReadAdapter _read;

    public AccountsController(
        IDatabaseRegistry registry,
        ISqlAccountApiClient api,
        ISqlAccountReadAdapter read)
    {
        _registry = registry;
        _api = api;
        _read = read;
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
            return await ListFromSqlApiAsync(db, cancellationToken).ConfigureAwait(false);

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

    /// <summary>Create a GL account (proxies POST /account).</summary>
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

        if (string.IsNullOrWhiteSpace(db.ApiBaseUrl) || string.IsNullOrWhiteSpace(db.ApiCredRef))
            return BadRequest(new { error = $"{db.DisplayName} has no SQL Accounting API credentials." });

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["dockey"] = body.DocKey,
            ["parent"] = body.Parent,
            ["code"] = body.Code.Trim(),
            ["description"] = body.Description.Trim(),
            ["description2"] = string.IsNullOrWhiteSpace(body.Description2) ? null : body.Description2.Trim(),
            ["acctype"] = string.IsNullOrWhiteSpace(body.AccType) ? null : body.AccType.Trim(),
            ["specialacctype"] = string.IsNullOrWhiteSpace(body.SpecialAccType) ? null : body.SpecialAccType.Trim(),
            ["tax"] = string.IsNullOrWhiteSpace(body.Tax) ? null : body.Tax.Trim(),
            ["cashflowtype"] = body.CashFlowType,
            ["sic"] = string.IsNullOrWhiteSpace(body.Sic) ? null : body.Sic.Trim()
        }, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var write = await _api.PostAsync(db, "/account", payload, cancellationToken).ConfigureAwait(false);
        if (!write.Success)
            return StatusCode(502, new { error = write.Message ?? "Create failed." });

        return Ok(new
        {
            ok = true,
            dbId = db.DbId,
            dbCode = db.DbCode,
            code = body.Code.Trim(),
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
