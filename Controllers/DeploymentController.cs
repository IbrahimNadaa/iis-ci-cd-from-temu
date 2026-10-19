using Microsoft.AspNetCore.Mvc;

namespace IisDeployApi.Controllers;

[ApiController]
[Route("api")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
public sealed class DeploymentController(DeploymentService service) : ControllerBase
{
    [HttpPost("uploads", Name = nameof(UploadZip))]
    [Tags("Uploads")]
    [EndpointSummary("Upload a build ZIP")]
    [EndpointDescription("Send a ZIP using the multipart field 'file'. Returns its absolute path on the IIS server.")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<UploadResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [IgnoreAntiforgeryToken] // Header authentication only; this API does not use cookies.
    public async Task<ActionResult<UploadResult>> UploadZip(IFormFile file, CancellationToken ct)
    {
        // IFormFile binding is inferred; adding [FromForm] here breaks Swagger's file picker.
        return Ok(await service.UploadAsync(file, ct));
    }

    [HttpPost("deployments", Name = nameof(DeployApplication))]
    [Tags("Deployments")]
    [EndpointSummary("Deploy a build to an IIS application")]
    [EndpointDescription("Use the path returned by upload. Backs up the current application, overwrites matching files, keeps other files, and restarts the pool.")]
    [Consumes("application/json")]
    [ProducesResponseType<DeploymentResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    public async Task<ActionResult<DeploymentResult>> DeployApplication([FromBody] DeployRequest request, CancellationToken ct)
    {
        return Ok(await service.DeployAsync(request, ct));
    }

    [HttpGet("iis/applications", Name = nameof(GetIisApplications))]
    [Tags("IIS")]
    [EndpointSummary("List IIS pools, applications, and backups")]
    [EndpointDescription("Returns all IIS application pools with application paths and related backups created by this API, newest first.")]
    [ProducesResponseType<IReadOnlyList<PoolInfo>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<PoolInfo>> GetIisApplications()
    {
        return Ok(service.GetApplications());
    }

    [HttpPost("restores", Name = nameof(RestoreApplication))]
    [Tags("Restores")]
    [EndpointSummary("Restore an IIS application from a backup")]
    [EndpointDescription("Use a backup path returned by deployment or discovery. Backs up the current application, replaces its contents with the selected snapshot, and restarts the pool. Files absent from the snapshot are removed.")]
    [Consumes("application/json")]
    [ProducesResponseType<DeploymentResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    public async Task<ActionResult<DeploymentResult>> RestoreApplication([FromBody] RestoreRequest request, CancellationToken ct)
    {
        return Ok(await service.RestoreAsync(request, ct));
    }

    [HttpGet("backups/download", Name = nameof(DownloadBackup))]
    [Tags("Backups")]
    [EndpointSummary("Download a backup ZIP by its full path")]
    [EndpointDescription("Pass backupZipPath from deployment or IIS discovery as a query parameter. Downloads a backup created by this API after verifying its metadata and SHA-256 hash.")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK, "application/zip")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<FileStreamResult> DownloadBackup([FromQuery] string backupZipPath, CancellationToken ct)
    {
        var stream = await service.OpenBackupDownloadAsync(backupZipPath, ct);
        Response.Headers.CacheControl = "no-store";
        return File(stream, "application/zip", Path.GetFileName(stream.Name));
    }
}
