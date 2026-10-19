using System.Security.Cryptography;
using System.Text;
using IisDeployApi;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("This API requires Windows with IIS installed.");

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Deployment").Get<DeploymentOptions>()
    ?? throw new InvalidOperationException("Missing Deployment configuration.");

if (string.IsNullOrEmpty(options.ApiKey) || options.ApiKey.Length < 32)
    throw new InvalidOperationException("Set Deployment__ApiKey to a secret of at least 32 characters.");
if (options.MaxUploadBytes <= 0 || options.MaxUploadBytes > long.MaxValue - 1024 * 1024 ||
    options.MaxExtractedBytes <= 0 || options.MaxArchiveEntries <= 0 || options.PoolTimeoutSeconds <= 0)
    throw new InvalidOperationException("Deployment limits must be positive and within range.");

var requestLimit = options.MaxUploadBytes + 1024 * 1024;
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = requestLimit);
builder.Services.Configure<IISServerOptions>(server => server.MaxRequestBodySize = requestLimit);
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = requestLimit);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<DeploymentService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger =>
{
    swagger.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "IIS Deployment API",
        Version = "v1",
        Description = "Upload builds, deploy IIS applications, list and download backups, and restore previous builds."
    });
    swagger.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = "Enter the API key configured with Deployment__ApiKey."
    });
    swagger.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ApiKey", document)] = []
    });
});

var app = builder.Build();
// Resolve now so invalid filesystem configuration fails before accepting requests.
_ = app.Services.GetRequiredService<DeploymentService>();
var expectedKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey));

// Serve documentation before key validation so a browser can load the Authorize dialog.
// Calls to the API endpoints still pass through key validation below.
app.UseSwagger();
app.UseSwaggerUI(swagger =>
{
    swagger.SwaggerEndpoint("v1/swagger.json", "IIS Deployment API v1");
    swagger.DocumentTitle = "IIS Deployment API";
});

app.Use(async (context, next) =>
{
    try
    {
        var provided = context.Request.Headers["X-Api-Key"];
        if (provided.Count != 1 || string.IsNullOrEmpty(provided[0]) ||
            !CryptographicOperations.FixedTimeEquals(expectedKeyHash,
                SHA256.HashData(Encoding.UTF8.GetBytes(provided[0]!))))
        {
            await Results.Problem(statusCode: 401, title: "A valid X-Api-Key header is required.")
                .ExecuteAsync(context);
            return;
        }
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        context.Abort();
    }
    catch (ApiException exception)
    {
        await Results.Problem(statusCode: exception.StatusCode, title: exception.Message)
            .ExecuteAsync(context);
    }
    catch (BadHttpRequestException exception)
    {
        await Results.Problem(statusCode: exception.StatusCode, title: "Invalid request.")
            .ExecuteAsync(context);
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Request failed: {Method} {Path}",
            context.Request.Method, context.Request.Path);
        if (context.Response.HasStarted)
        {
            // A failed file transfer cannot be replaced with a JSON error mid-response.
            context.Abort();
            return;
        }
        await Results.Problem(statusCode: 500,
            title: "The operation failed. Check the server log for details.").ExecuteAsync(context);
    }
});

app.MapPost("/api/uploads", async (IFormFile file, DeploymentService service, CancellationToken ct) =>
    Results.Ok(await service.UploadAsync(file, ct)))
    .WithName("UploadZip")
    .WithTags("Uploads")
    .WithSummary("Upload a build ZIP")
    .WithDescription("Send a ZIP using the multipart field 'file'. Returns its absolute path on the IIS server.")
    .Produces<UploadResult>()
    .ProducesProblem(400)
    .ProducesProblem(401)
    .ProducesProblem(413)
    .ProducesProblem(500)
    .DisableAntiforgery(); // Header authentication only; this API does not use cookies.

app.MapPost("/api/deployments", async (DeployRequest request, DeploymentService service, CancellationToken ct) =>
    Results.Ok(await service.DeployAsync(request, ct)))
    .WithName("DeployApplication")
    .WithTags("Deployments")
    .WithSummary("Deploy a build to an IIS application")
    .WithDescription("Use the path returned by upload. Backs up the current application, overwrites matching files, keeps other files, and restarts the pool.")
    .Produces<DeploymentResult>()
    .ProducesProblem(400)
    .ProducesProblem(401)
    .ProducesProblem(404)
    .ProducesProblem(409)
    .ProducesProblem(413)
    .ProducesProblem(500);

app.MapGet("/api/iis/applications", (DeploymentService service) => Results.Ok(service.GetApplications()))
    .WithName("GetIisApplications")
    .WithTags("IIS")
    .WithSummary("List IIS pools, applications, and backups")
    .WithDescription("Returns all IIS application pools with application paths and related backups created by this API, newest first.")
    .Produces<IReadOnlyList<PoolInfo>>()
    .ProducesProblem(401)
    .ProducesProblem(500);

app.MapPost("/api/restores", async (RestoreRequest request, DeploymentService service, CancellationToken ct) =>
    Results.Ok(await service.RestoreAsync(request, ct)))
    .WithName("RestoreApplication")
    .WithTags("Restores")
    .WithSummary("Restore an IIS application from a backup")
    .WithDescription("Use a backup path returned by deployment or discovery. Backs up the current application, replaces its contents with the selected snapshot, and restarts the pool. Files absent from the snapshot are removed.")
    .Produces<DeploymentResult>()
    .ProducesProblem(400)
    .ProducesProblem(401)
    .ProducesProblem(404)
    .ProducesProblem(409)
    .ProducesProblem(413)
    .ProducesProblem(500);

app.MapGet("/api/backups/download", async (string backupZipPath, DeploymentService service,
    HttpContext context, CancellationToken ct) =>
{
    var stream = await service.OpenBackupDownloadAsync(backupZipPath, ct);
    context.Response.Headers.CacheControl = "no-store";
    return TypedResults.File(stream, contentType: "application/zip",
        fileDownloadName: Path.GetFileName(stream.Name));
})
    .WithName("DownloadBackup")
    .WithTags("Backups")
    .WithSummary("Download a backup ZIP by its full path")
    .WithDescription("Pass backupZipPath from deployment or IIS discovery as a query parameter. Downloads a backup created by this API after verifying its metadata and SHA-256 hash.")
    .Produces<Stream>(contentType: "application/zip")
    .ProducesProblem(400)
    .ProducesProblem(401)
    .ProducesProblem(404)
    .ProducesProblem(500);

app.Run();
