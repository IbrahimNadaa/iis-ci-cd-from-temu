using System.Security.Cryptography;
using System.Text;
using IisDeployApi;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("This API requires Windows with IIS installed.");

var builder = WebApplication.CreateBuilder(args);
var configuredBasePath = (builder.Configuration["Hosting:PathBase"] ?? "").Trim().TrimEnd('/');
if (configuredBasePath.Length > 0 &&
    (!configuredBasePath.StartsWith('/') || configuredBasePath.Contains("//", StringComparison.Ordinal) ||
     configuredBasePath.IndexOfAny(['\\', '?', '#']) >= 0 || configuredBasePath.Any(char.IsControl) ||
     configuredBasePath.Split('/').Any(segment => segment is "." or "..")))
    throw new InvalidOperationException("Hosting:PathBase must be a path such as /cicd, or empty for automatic detection.");
var configuredPathBase = new PathString(configuredBasePath);

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
builder.Services.AddControllers();
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

if (configuredPathBase.HasValue)
{
    // Extract a prefix still present in the forwarded path before matching controller routes.
    app.UsePathBase(configuredPathBase);
    app.Use((context, next) =>
    {
        // Some proxies strip the prefix without setting PathBase. IIS may already set it.
        if (!context.Request.PathBase.HasValue)
            context.Request.PathBase = configuredPathBase;
        return next(context);
    });
}
// Keep routing after path normalization; implicit routing would run too early.
app.UseRouting();

// Resolve now so invalid filesystem configuration fails before accepting requests.
_ = app.Services.GetRequiredService<DeploymentService>();
var expectedKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey));

// Serve documentation before key validation so a browser can load the Authorize dialog.
// Calls to the API endpoints still pass through key validation below.
app.UseSwagger(swagger =>
{
    swagger.PreSerializeFilters.Add((document, request) =>
    {
        // Prefer the normalized base path. If the host supplies none, resolve against the
        // public /<application>/swagger/v1/swagger.json URL instead of dropping to '/'.
        document.Servers =
        [
            new OpenApiServer
            {
                Url = request.PathBase.HasValue ? request.PathBase.ToUriComponent() : "../../"
            }
        ];
        request.HttpContext.Response.Headers.CacheControl = "no-store";
    });
});
app.UseSwaggerUI(swagger =>
{
    swagger.RoutePrefix = "swagger";
    // Resolve from /<application>/swagger/index.html, including nested application paths.
    swagger.SwaggerEndpoint("./v1/swagger.json", "IIS Deployment API v1");
    SwaggerUiTheme.Apply(swagger, builder.Configuration, builder.Environment.EnvironmentName);
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

app.MapControllers();

app.Run();
