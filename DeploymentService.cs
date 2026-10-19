using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.Administration;

namespace IisDeployApi;

public sealed class DeploymentService : IDisposable
{
    private readonly DeploymentOptions _options;
    private readonly ILogger<DeploymentService> _logger;
    private readonly IisAccess _iis;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly FileStream _instanceLock;
    private readonly string[] _allowedRoots;
    private readonly string[] _apiRoots;
    private readonly string _uploads;
    private readonly string _backups;
    private readonly string _staging;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DeploymentService(DeploymentOptions options, IWebHostEnvironment environment,
        ILogger<DeploymentService> logger)
    {
        _options = options;
        _logger = logger;
        _iis = new IisAccess(options);
        _allowedRoots = options.AllowedWebRoots.Select(SafeFiles.LocalPath).ToArray();
        _apiRoots = [SafeFiles.LocalPath(environment.ContentRootPath), SafeFiles.LocalPath(AppContext.BaseDirectory)];
        options.DataRoot = SafeFiles.LocalPath(options.DataRoot);
        if (_allowedRoots.Length == 0 || _allowedRoots.Any(root => root == Path.GetPathRoot(root)))
            throw new InvalidOperationException("Configure AllowedWebRoots with specific application directories.");
        if (_allowedRoots.Concat(_apiRoots).Any(root => SafeFiles.Overlaps(root, options.DataRoot)))
            throw new InvalidOperationException("DataRoot must be outside AllowedWebRoots and the API's own directories.");
        SafeFiles.RejectReparseAncestors(options.DataRoot);
        _uploads = Path.Combine(options.DataRoot, "uploads");
        _backups = Path.Combine(options.DataRoot, "backups");
        _staging = Path.Combine(options.DataRoot, "staging");
        foreach (var directory in new[] { _uploads, _backups, _staging })
        {
            SafeFiles.RejectReparseAncestors(directory);
            Directory.CreateDirectory(directory);
        }
        // Prevent two instances that share this data directory from changing IIS concurrently.
        var lockPath = Path.Combine(options.DataRoot, "instance.lock");
        SafeFiles.RejectReparseAncestors(lockPath);
        _instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async Task<UploadResult> UploadAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0 || !string.Equals(Path.GetExtension(file.FileName), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(400, "Upload a non-empty .zip file using the multipart field 'file'.");
        if (file.Length > _options.MaxUploadBytes)
            throw new ApiException(413, "The ZIP exceeds the upload limit.");

        SafeFiles.RejectReparseAncestors(_uploads);
        var path = Path.Combine(_uploads, $"{Guid.NewGuid():N}.zip");
        var temporary = path + ".partial";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, useAsync: true))
                await file.CopyToAsync(stream, ct);
            using (var archive = ZipFile.OpenRead(temporary))
            {
                if (archive.Entries.Count == 0)
                    throw new ApiException(400, "The ZIP is empty.");
                if (archive.Entries.Count > _options.MaxArchiveEntries)
                    throw new ApiException(413, "The ZIP contains too many entries.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            _logger.LogInformation("Stored uploaded ZIP {ZipFilePath}", path);
            return new UploadResult(path, new FileInfo(path).Length);
        }
        catch (InvalidDataException)
        {
            throw new ApiException(400, "The uploaded file is not a readable ZIP archive.");
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    public IReadOnlyList<PoolInfo> GetApplications() => _iis.GetApplications(ReadBackups);

    public Task<DeploymentResult> DeployAsync(DeployRequest request, CancellationToken ct) =>
        ExecuteAsync(request.ZipFilePath, request.WebAppPath, request.ApplicationPoolName, restore: false, ct);

    public Task<DeploymentResult> RestoreAsync(RestoreRequest request, CancellationToken ct) =>
        ExecuteAsync(request.BackupZipPath, request.WebAppPath, request.ApplicationPoolName, restore: true, ct);

    private async Task<DeploymentResult> ExecuteAsync(string sourcePath, string webAppPath,
        string poolName, bool restore, CancellationToken ct)
    {
        if (!await _operationGate.WaitAsync(0, ct))
            throw new ApiException(409, "Another deployment or restore is running. Retry after it finishes.");
        string? work = null;
        var preserveWork = false;
        try
        {
            var target = ValidateTarget(webAppPath, poolName);
            var source = SafeFiles.LocalPath(sourcePath);
            if (restore)
                ValidateBackup(source, target, poolName);
            else if (!string.Equals(Path.GetDirectoryName(source), _uploads, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
                throw new ApiException(400, "ZipFilePath must be a ZIP returned by /api/uploads.");
            SafeFiles.RejectReparseAncestors(source);
            if (!File.Exists(source)) throw new ApiException(404, "The ZIP file was not found.");

            SafeFiles.RejectReparseAncestors(_staging);
            work = Path.Combine(_staging, Guid.NewGuid().ToString("N"));
            var incoming = Path.Combine(work, "incoming");
            var previous = Path.Combine(work, "previous");
            try { SafeFiles.ExtractZip(source, incoming, _options, requireFiles: !restore); }
            catch (InvalidDataException) { throw new ApiException(400, "The ZIP is corrupt or uses unsupported compression."); }
            ct.ThrowIfCancellationRequested();
            ValidateTarget(target, poolName);
            var originalState = await _iis.GetStableStateAsync(poolName);
            ct.ThrowIfCancellationRequested();

            // From this point, finish or roll back even if the client disconnects.
            BackupInfo? backup = null;
            var filesMayHaveChanged = false;
            try
            {
                await _iis.SetStateAsync(poolName, ObjectState.Stopped);
                SafeFiles.CopyTree(target, previous, _options);
                backup = SaveBackup(previous, target, poolName, restore ? "before-restore" : "before-deployment");

                filesMayHaveChanged = true;
                if (restore) SafeFiles.ClearDirectory(target);
                SafeFiles.CopyTree(incoming, target, _options);
                await _iis.SetStateAsync(poolName, ObjectState.Started);
                _logger.LogInformation("{Operation} completed for {WebAppPath} in {Pool}. Previous build: {Backup}",
                    restore ? "Restore" : "Deployment", target, poolName, backup.ZipFilePath);
                return new DeploymentResult(restore ? "restore" : "deploy", target, poolName, "Started", backup);
            }
            catch (Exception failure)
            {
                _logger.LogError(failure, "Operation failed for {WebAppPath}; restoring previous state", target);
                try
                {
                    if (filesMayHaveChanged)
                    {
                        await _iis.SetStateAsync(poolName, ObjectState.Stopped);
                        SafeFiles.ClearDirectory(target);
                        SafeFiles.CopyTree(previous, target, _options);
                    }
                    await _iis.SetStateAsync(poolName, originalState);
                }
                catch (Exception recoveryFailure)
                {
                    preserveWork = true;
                    _logger.LogCritical(recoveryFailure,
                        "Automatic recovery failed for {WebAppPath}. Backup: {Backup}; working directory: {Work}",
                        target, backup?.ZipFilePath, work);
                    throw new ApiException(500,
                        $"The operation and automatic recovery failed. Manual recovery is required. Backup: {backup?.ZipFilePath ?? "not created"}. Working directory: {work}");
                }
                throw new ApiException(500,
                    $"The operation failed; previous files and pool state were preserved or restored. Backup: {backup?.ZipFilePath ?? "not created"}. Check the server log.");
            }
        }
        finally
        {
            if (work is not null && !preserveWork) TryDeleteDirectory(work);
            _operationGate.Release();
        }
    }

    private string ValidateTarget(string path, string poolName)
    {
        if (string.IsNullOrWhiteSpace(poolName)) throw new ApiException(400, "ApplicationPoolName is required.");
        var target = SafeFiles.LocalPath(path);
        if (!_allowedRoots.Any(root => SafeFiles.IsWithin(target, root)))
            throw new ApiException(400, "WebAppPath must be inside a configured AllowedWebRoots directory.");
        if (_apiRoots.Any(root => SafeFiles.Overlaps(target, root)) || SafeFiles.Overlaps(target, _options.DataRoot))
            throw new ApiException(400, "The API cannot deploy over itself or its data directory.");
        SafeFiles.RejectReparseAncestors(target);
        if (!Directory.Exists(target)) throw new ApiException(404, "The web application directory was not found.");
        _iis.ValidateTarget(target, poolName);
        return target;
    }

    private BackupInfo SaveBackup(string snapshot, string target, string poolName, string reason)
    {
        var directory = BackupDirectory(target, poolName);
        SafeFiles.RejectReparseAncestors(directory);
        Directory.CreateDirectory(directory);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var zipPath = Path.Combine(directory, id + ".zip");
        var temporaryZip = zipPath + ".partial";
        var metadataPath = Path.Combine(directory, id + ".json");
        try
        {
            SafeFiles.CreateZip(snapshot, temporaryZip);
            var backup = new BackupInfo(id, target, poolName, zipPath, DateTimeOffset.UtcNow,
                new FileInfo(temporaryZip).Length, reason, HashFile(temporaryZip));
            File.WriteAllText(metadataPath + ".partial", JsonSerializer.Serialize(backup, JsonOptions));
            File.Move(temporaryZip, zipPath);
            File.Move(metadataPath + ".partial", metadataPath);
            return backup;
        }
        finally
        {
            TryDeleteFile(temporaryZip);
            TryDeleteFile(metadataPath + ".partial");
        }
    }

    private IReadOnlyList<BackupInfo> ReadBackups(string target, string poolName)
    {
        if (string.IsNullOrEmpty(target)) return [];
        var directory = BackupDirectory(target, poolName);
        SafeFiles.RejectReparseAncestors(directory);
        if (!Directory.Exists(directory)) return [];
        var result = new List<BackupInfo>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                SafeFiles.RejectReparseAncestors(file);
                var backup = JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(file), JsonOptions);
                if (backup is not null &&
                    string.Equals(backup.WebAppPath, target, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(backup.ApplicationPoolName, poolName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(backup.ZipFilePath, Path.ChangeExtension(file, ".zip"), StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(backup.ZipFilePath)) result.Add(backup);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            { _logger.LogWarning(ex, "Could not read backup metadata {MetadataPath}", file); }
        }
        return result.OrderByDescending(backup => backup.CreatedAtUtc).ToList();
    }

    private void ValidateBackup(string source, string target, string poolName)
    {
        var backup = ReadBackups(target, poolName).FirstOrDefault(item =>
            string.Equals(item.ZipFilePath, source, StringComparison.OrdinalIgnoreCase))
            ?? throw new ApiException(404, "This backup was not found for the specified application and pool.");
        SafeFiles.RejectReparseAncestors(source);
        if (!string.Equals(HashFile(source), backup.Sha256, StringComparison.Ordinal))
            throw new ApiException(400, "The backup ZIP failed its integrity check.");
    }

    private string BackupDirectory(string target, string poolName) => Path.Combine(_backups,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            target.ToUpperInvariant() + "\n" + poolName.ToUpperInvariant()))).ToLowerInvariant());

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logger.LogWarning(ex, "Could not remove temporary file {Path}", path); }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            SafeFiles.ClearDirectory(path);
            Directory.Delete(path);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not remove staging directory {Path}", path); }
    }

    public void Dispose()
    {
        _instanceLock.Dispose();
        _operationGate.Dispose();
    }
}
