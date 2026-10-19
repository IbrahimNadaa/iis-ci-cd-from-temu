namespace IisDeployApi;

public sealed class DeploymentOptions
{
    public string ApiKey { get; set; } = "";
    public string DataRoot { get; set; } = @"C:\IisDeployData";
    public string[] AllowedWebRoots { get; set; } = [];
    public long MaxUploadBytes { get; set; } = 256L * 1024 * 1024;
    public long MaxExtractedBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaxArchiveEntries { get; set; } = 20_000;
    public int PoolTimeoutSeconds { get; set; } = 60;
}

public sealed record DeployRequest(
    string ZipFilePath, string WebAppPath, string ApplicationPoolName);

public sealed record RestoreRequest(
    string BackupZipPath, string WebAppPath, string ApplicationPoolName);

public sealed record UploadResult(string ZipFilePath, long SizeBytes);

public sealed record BackupInfo(
    string Id, string WebAppPath, string ApplicationPoolName,
    string ZipFilePath, DateTimeOffset CreatedAtUtc, long SizeBytes,
    string Reason, string Sha256);

public sealed record ApplicationInfo(
    string SiteName, string ApplicationPath, string WebAppPath,
    IReadOnlyList<BackupInfo> Backups);

public sealed record PoolInfo(
    string Name, string State, IReadOnlyList<ApplicationInfo> Applications);

public sealed record DeploymentResult(
    string Operation, string WebAppPath, string ApplicationPoolName,
    string ApplicationPoolState, BackupInfo PreviousBuildBackup);

public sealed class ApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
