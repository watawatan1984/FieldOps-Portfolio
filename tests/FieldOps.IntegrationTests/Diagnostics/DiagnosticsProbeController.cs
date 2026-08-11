using FieldOps.Domain.Common;
using FieldOps.Features.Abstractions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.IntegrationTests.Diagnostics;

[ApiController]
[AllowAnonymous]
[Route("diagnostics-probe")]
public sealed class DiagnosticsProbeController : ControllerBase
{
    [HttpGet("ok")]
    public IActionResult OkProbe() => Ok();

    [HttpGet("path/{value}")]
    public IActionResult PathProbe(string value)
    {
        _ = value;
        return Ok();
    }

    [HttpGet("mutation")]
    public async Task<IActionResult> MutationProbe(
        [FromServices] IMutationExecutor mutationExecutor,
        CancellationToken cancellationToken)
    {
        await mutationExecutor.ExecuteAsync(
            "diagnostics.request-mutation",
            _ => Task.FromResult(true),
            cancellationToken);
        return Ok();
    }

    [HttpGet("domain-error")]
    public IActionResult DomainError() => throw new DomainException("Domain validation failed.");

    [HttpGet("concurrency-error")]
    public IActionResult ConcurrencyError() => throw new DbUpdateConcurrencyException("Concurrent write failed.");

    [HttpGet("forbidden")]
    public IActionResult Forbidden() => Forbid();

    [HttpGet("authorization-error")]
    public IActionResult AuthorizationError() => throw new UnauthorizedAccessException("Authorization failed.");

    [HttpGet("not-found")]
    public IActionResult Missing() => NotFound();

    [HttpGet("missing-error")]
    public IActionResult MissingError() => throw new KeyNotFoundException("Resource was not found.");

    [HttpGet("unhandled")]
    public IActionResult Unhandled() => throw new InvalidOperationException("server-only-diagnostic-secret");
}