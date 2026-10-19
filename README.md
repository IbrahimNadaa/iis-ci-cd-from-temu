# IIS Deployment API

A small ASP.NET Core 10 API for a Windows server with IIS. It has five endpoints and stores uploads, backup ZIPs, and backup metadata on disk. There is no database.

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/api/uploads` | Accept a ZIP and return its absolute server path. |
| POST | `/api/deployments` | Back up the current application, overwrite files from the uploaded ZIP, then start the pool. Files absent from the ZIP remain. |
| GET | `/api/iis/applications` | List all IIS pools, their applications and physical paths, and all backups made by this API for each application. |
| POST | `/api/restores` | Back up the current application, replace its contents with a saved backup, then start the pool. |
| GET | `/api/backups/download?backupZipPath=...` | Download a saved backup ZIP by its full server path. |

Every API operation requires the `X-Api-Key` header.

All five endpoints are implemented in `Controllers/DeploymentController.cs`. The controller binds incoming requests and returns responses through `DeploymentService`, which handles IIS, files, and backups. `Program.cs` registers and maps controllers and configures API-key middleware and Swagger.

## Setup

1. Use Windows with IIS installed, including the **Management Scripts and Tools** feature, and the .NET 10 SDK for running from source.
2. Run the API under an account permitted to read IIS configuration, start and stop the target pool, read and modify the application directory, and create files in `DataRoot`.
3. Set `Deployment:AllowedWebRoots` in `appsettings.json` to the directories containing your applications. The default is `C:\inetpub\wwwroot`. Set `DataRoot` to a private directory outside your websites and outside the API's directory. Its default is `C:\IisDeployData`.
4. Give each application a dedicated pool. Keep the API in its own process or its own IIS application and pool, outside the applications it manages.
5. Set a secret using the environment variable `Deployment__ApiKey`. The API refuses to start without a key of at least 32 characters.

Example PowerShell session, from this project's directory, when you are ready to run it:

```powershell
$keyBytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($keyBytes)
$rng.Dispose()
$env:Deployment__ApiKey = [Convert]::ToBase64String($keyBytes)

dotnet run --project .\IisDeployApi.csproj --no-launch-profile
```

Keep the key to use in your client. Environment variables set in this session apply to the process launched from it; configure a persistent secret in your hosting environment for a service deployment.

The address configured in `appsettings.json` is `http://127.0.0.1:5080`. The command above uses that address. The included Visual Studio launch profile instead uses `https://localhost:50781` and `http://localhost:50782` and opens Swagger automatically. For remote access, configure HTTPS, an appropriate host in `AllowedHosts`, and private network access. If hosting behind IIS or another proxy, configure its upload limit and request timeout too. IIS request filtering has its own `maxAllowedContentLength` limit; the limits in `Program.cs` do not override it.

## Swagger UI

After starting the API with the command above, open `http://127.0.0.1:5080/swagger`. When using the included Visual Studio launch profile, use `https://localhost:50781/swagger` instead. Click **Authorize**, enter your `Deployment__ApiKey` value, and click **Authorize** again. Swagger sends it in the `X-Api-Key` header for all five operations. Use **Try it out** to choose a ZIP file, enter a JSON request body, or provide a backup path for download, then **Execute** to submit it.

Swagger includes endpoint descriptions, request/response schemas, a file picker for uploads, and documented error responses. Deployment and restore requests submitted from Swagger perform real changes to IIS and application files.

The OpenAPI JSON document is available at `/swagger/v1/swagger.json`. Swagger UI and its JSON document are available without a key in all environments so that the browser can load the authorization dialog; all API operations still require a valid key. The key is never embedded in the document or page. If hosted under an IIS application path, append `/swagger` to that application's URL.

## 1. Upload

Send `multipart/form-data` with one field named `file`:

```powershell
curl.exe http://127.0.0.1:5080/api/uploads `
  -H "X-Api-Key: $env:Deployment__ApiKey" `
  -F "file=@C:\Builds\release.zip"
```

Example response:

```json
{
  "zipFilePath": "C:\\IisDeployData\\uploads\\f84f0b1833f6476fb38d7bad915ab8ae.zip",
  "sizeBytes": 153824
}
```

Uploads get generated names; the original filename cannot choose a server location. The ZIP's central directory is checked on upload. Its entry paths and expanded contents are validated before deployment.

The ZIP must contain the **published app files at its root**, for example `web.config`, `MyApp.dll`, and `wwwroot/`. A wrapping `publish/` directory would be copied as a directory named `publish`.

## 2. Deploy

`POST /api/deployments` with `Content-Type: application/json` and this body:

```json
{
  "zipFilePath": "C:\\IisDeployData\\uploads\\f84f0b1833f6476fb38d7bad915ab8ae.zip",
  "webAppPath": "C:\\inetpub\\wwwroot\\MyApp",
  "applicationPoolName": "MyAppPool"
}
```

Use the upload path returned by endpoint 1. All paths in JSON refer to the IIS server's filesystem.

Deployment validates and extracts the ZIP into staging, stops the pool and waits for its workers to exit, copies the existing application into a temporary snapshot, saves that snapshot as a backup ZIP, then copies incoming files over the application and starts the pool. The existing directory name and IIS application name are kept.

Files in the ZIP overwrite matching files, including `web.config` and `appsettings.json`. Files absent from the ZIP remain. A file/directory type conflict fails deployment and triggers recovery. Keep server-specific settings out of the ZIP if you want them preserved.

Example response:

```json
{
  "operation": "deploy",
  "webAppPath": "C:\\inetpub\\wwwroot\\MyApp",
  "applicationPoolName": "MyAppPool",
  "applicationPoolState": "Started",
  "previousBuildBackup": {
    "id": "20261019T090000000Z-523899d809d34bea8582e4e97c70468c",
    "webAppPath": "C:\\inetpub\\wwwroot\\MyApp",
    "applicationPoolName": "MyAppPool",
    "zipFilePath": "C:\\IisDeployData\\backups\\<application-key>\\20261019T090000000Z-523899d809d34bea8582e4e97c70468c.zip",
    "createdAtUtc": "2026-10-19T09:00:00+00:00",
    "sizeBytes": 145001,
    "reason": "before-deployment",
    "sha256": "<SHA-256 of the backup ZIP>"
  }
}
```

The response examples use illustrative IDs and hashes. Use actual values from your server.

## 3. Discover IIS applications and backups

```powershell
curl.exe http://127.0.0.1:5080/api/iis/applications `
  -H "X-Api-Key: $env:Deployment__ApiKey"
```

Example response structure:

```json
[
  {
    "name": "MyAppPool",
    "state": "Started",
    "applications": [
      {
        "siteName": "My Website",
        "applicationPath": "/",
        "webAppPath": "C:\\inetpub\\wwwroot\\MyApp",
        "backups": []
      }
    ]
  }
]
```

Each `backups` item has the same shape as `previousBuildBackup` above. Backups are ordered newest first and associated with the physical application path and pool name. Pools with no applications are included with an empty array. Discovery includes applications outside `AllowedWebRoots`; deployments and restores are restricted to the configured roots. This lists backups created by this API, not unrelated ZIP files elsewhere on the server.

## 4. Restore

`POST /api/restores` with `Content-Type: application/json`:

```json
{
  "backupZipPath": "C:\\IisDeployData\\backups\\<application-key>\\<backup-id>.zip",
  "webAppPath": "C:\\inetpub\\wwwroot\\MyApp",
  "applicationPoolName": "MyAppPool"
}
```

Use a backup path returned by endpoint 2 or 3. Restore verifies that the ZIP belongs to that application and pool and matches its saved SHA-256 hash. It backs up the current app with reason `before-restore`, then replaces the app's files and directories with the selected snapshot. **Files added after that snapshot are removed.** The current version's backup is returned as `previousBuildBackup`; `operation` is `restore`.

## 5. Download a backup

`GET /api/backups/download` takes a required query parameter named `backupZipPath`. Use a backup's `zipFilePath` returned by deployment, restore, or IIS discovery. In Swagger, choose **DownloadBackup**, click **Try it out**, paste the full path, and execute. Use the **Download file** link in the response to save the ZIP.

PowerShell example (replace the placeholder with an actual backup path):

```powershell
$backupPath = 'C:\IisDeployData\backups\<application-key>\<backup-id>.zip'
curl.exe --fail-with-body --get http://127.0.0.1:5080/api/backups/download `
  -H "X-Api-Key: $env:Deployment__ApiKey" `
  --data-urlencode "backupZipPath=$backupPath" `
  --output .\backup.zip
```

The parameter is a server path; `--data-urlencode` handles spaces and backslashes. The response streams `application/zip` with `Content-Disposition: attachment` and the original backup filename. It is marked `Cache-Control: no-store`. Only registered backup ZIPs under this API's backups directory are accepted, and the saved metadata, file size, and SHA-256 hash must match. No application path or pool name is needed, and downloading does not modify IIS. Missing backup files or metadata return `404`; invalid paths, metadata, or integrity checks return `400`.

## Recovery and operating limits

- Deployment and restore are synchronous and allow one operation at a time. A concurrent mutation gets `409`. A file lock prevents multiple API instances from sharing the same `DataRoot`; run one instance per IIS server.
- A failure after file modification triggers a full restore from the temporary snapshot and returns the pool to its original started/stopped state. Once pool changes begin, a client disconnect does not cancel recovery. A successful operation starts the pool even if it was initially stopped.
- If recovery fails, the response identifies the saved backup and retained staging directory for manual recovery. Errors are logged server-side. Process termination or a server crash during an operation requires manual recovery; this API has no durable job queue.
- A success response confirms the pool reached `Started`; it does not perform an HTTP health check of the app.
- Defaults: 256 MiB uploaded ZIP, 2 GiB uncompressed contents/application snapshot, 20,000 archive entries, and a 60-second timeout for each pool-state wait. These are configurable in `appsettings.json`.
- Backups include application files and empty directories. They do not include database contents, IIS configuration, file ACLs, or data outside the app directory. A backup briefly stops the application; larger apps take longer to back up. Allow space for the upload, extracted ZIP, previous snapshot, and backup ZIP.
- Additional virtual directories, shared pools, and other IIS applications/virtual directories rooted inside the target directory are rejected. Symbolic links, junctions, UNC paths, ZIP traversal, and duplicate ZIP entry paths are also rejected.
- The API must be the only writer during deployment. Restrict application, staging, upload, and backup permissions to trusted accounts. Backups may contain credentials from configuration files.
- Uploads and backups remain on disk until an administrator removes them. There is no automatic retention policy or cleanup endpoint. Keep each backup's `.zip` and `.json` files together. Renaming a pool or moving an application changes its backup association.
- Errors use Problem Details JSON: `400` invalid input/archive, `401` invalid key, `404` missing file/pool/backup, `409` busy or unsupported IIS layout, `413` size/entry limit, and `500` operational failure. A limit failure while taking a snapshot is reported as `500` after restoring the original pool state.

## Project status

Source was written and reviewed without building, running the API, changing IIS, or adding tests, as requested. Validate it on your IIS server before using it for deployments.

## References

- [Microsoft: IIS management API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.administration?view=iis-dotnet)
- [Microsoft: ASP.NET Core file uploads](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0)
- [Microsoft.Web.Administration package](https://www.nuget.org/packages/Microsoft.Web.Administration/11.1.0)
- [Swashbuckle setup and Swagger UI](https://github.com/domaindrivendev/Swashbuckle.AspNetCore/blob/v10.3.0/README.md)
- [Microsoft: file results in Minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses?view=aspnetcore-10.0#file-result-return-values)
- [Microsoft: controller-based web APIs](https://learn.microsoft.com/en-us/aspnet/core/web-api/?view=aspnetcore-10.0)
