using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlAccountSync.Authorization;
using SqlAccountSync.Services.Queue;

namespace SqlAccountSync.Controllers;

/// <summary>
/// Development-only explicit single-job process. Does not enable background workers
/// and does not scan other pending jobs.
/// </summary>
[ApiController]
[Route("api/dev/jobs")]
[Authorize(Policy = PolicyNames.Admin)]
public sealed class DevJobProcessController : ControllerBase
{
    private readonly IDevSingleJobProcessor _processor;
    private readonly IHostEnvironment _env;

    public DevJobProcessController(IDevSingleJobProcessor processor, IHostEnvironment env)
    {
        _processor = processor;
        _env = env;
    }

    /// <summary>
    /// Claim and process exactly one job id through SyncOrchestrator / TransactionEngine.
    /// Refuses outside Development; destination must be MIRACLE or ONYAT.
    /// </summary>
    [HttpPost("{jobId:int}/process")]
    public async Task<IActionResult> ProcessExactAsync(int jobId, CancellationToken cancellationToken)
    {
        if (!_env.IsDevelopment())
            return NotFound();

        var result = await _processor.ProcessExactAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            if (result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return NotFound(result);
            if (result.Message.Contains("only in Development", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("AllowDevSingleJobProcess", StringComparison.OrdinalIgnoreCase))
                return StatusCode(StatusCodes.Status403Forbidden, result);
            return BadRequest(result);
        }

        return Ok(result);
    }
}
