using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Authorization;
using SqlAccountSync.Services.Databases;
using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Controllers;

/// <summary>
/// Local Website SDK diagnostics (Development only). Status is read-only config;
/// probe performs Login + SELECT-only + Logout. Never creates/updates/deletes BizObject rows.
/// </summary>
[ApiController]
[Route("api/sdk")]
[Authorize(Policy = PolicyNames.Admin)]
public sealed class SdkDiagnosticsController : ControllerBase
{
    private readonly ISqlAccSessionDiagnostics _diagnostics;
    private readonly IDatabaseRegistry _registry;
    private readonly IHostEnvironment _env;

    public SdkDiagnosticsController(
        ISqlAccSessionDiagnostics diagnostics,
        IDatabaseRegistry registry,
        IHostEnvironment env)
    {
        _diagnostics = diagnostics;
        _registry = registry;
        _env = env;
    }

    /// <summary>Effective write provider + SDK session configuration (no COM, no password values).</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        if (!_env.IsDevelopment())
            return NotFound();
        return Ok(_diagnostics.GetProviderStatus());
    }

    /// <summary>
    /// Login → SELECT COUNT(*) FROM RDB$DATABASE → release session for a registered company.
    /// Pass dbId or dbCode (e.g. MIRACLE / ONYAT). No CUD.
    /// </summary>
    [HttpGet("session-probe")]
    public async Task<IActionResult> SessionProbeAsync(
        [FromQuery] int? dbId,
        [FromQuery] string? dbCode,
        CancellationToken cancellationToken)
    {
        if (!_env.IsDevelopment())
            return NotFound();

        Models.RegisteredDatabase? db = null;
        if (dbId is > 0)
        {
            db = await _registry.GetAsync(dbId.Value, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(dbCode))
        {
            var code = dbCode.Trim();
            var all = await _registry.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            db = all.FirstOrDefault(d =>
                string.Equals(d.DbCode, code, StringComparison.OrdinalIgnoreCase));
        }

        if (db is null)
            return NotFound(new { error = "Database not found. Pass dbId or dbCode." });

        var result = await _diagnostics.ProbeLoginReadLogoutAsync(db, cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : StatusCode(502, result);
    }
}
