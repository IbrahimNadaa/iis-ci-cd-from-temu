using System.Diagnostics;
using Microsoft.Web.Administration;

namespace IisDeployApi;

internal sealed class IisAccess(DeploymentOptions options)
{
    public void ValidateTarget(string webAppPath, string poolName)
    {
        using var manager = new ServerManager();
        if (manager.ApplicationPools[poolName] is null)
            throw new ApiException(404, "The IIS application pool was not found.");

        var applications = manager.Sites.SelectMany(site => site.Applications).ToList();
        var matches = applications.Where(application =>
            string.Equals(application.ApplicationPoolName, poolName, StringComparison.OrdinalIgnoreCase) &&
            application.VirtualDirectories["/"] is { } root &&
            string.Equals(PhysicalPath(root), webAppPath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
            throw new ApiException(400, "WebAppPath must match exactly one IIS application's root directory in the specified pool.");

        var target = matches[0];
        if (applications.Count(application => string.Equals(application.ApplicationPoolName, poolName,
                StringComparison.OrdinalIgnoreCase)) != 1)
            throw new ApiException(409, "Use a dedicated application pool; this pool is shared by multiple applications.");
        if (target.VirtualDirectories.Any(directory => directory.Path != "/"))
            throw new ApiException(409, "Applications with additional virtual directories are not supported.");

        foreach (var application in applications)
        foreach (var directory in application.VirtualDirectories)
        {
            if (ReferenceEquals(application, target) && directory.Path == "/") continue;
            var otherPath = PhysicalPath(directory);
            if (SafeFiles.IsWithin(otherPath, webAppPath))
                throw new ApiException(409, "Another IIS application or virtual directory uses this directory or a directory inside it.");
        }
    }

    public IReadOnlyList<PoolInfo> GetApplications(Func<string, string, IReadOnlyList<BackupInfo>> getBackups)
    {
        using var manager = new ServerManager();
        return manager.ApplicationPools.Select(pool => new PoolInfo(
            pool.Name, pool.State.ToString(),
            manager.Sites.SelectMany(site => site.Applications
                .Where(application => string.Equals(application.ApplicationPoolName, pool.Name,
                    StringComparison.OrdinalIgnoreCase))
                .Select(application =>
                {
                    var root = application.VirtualDirectories["/"];
                    var path = root is null ? "" : PhysicalPath(root);
                    return new ApplicationInfo(site.Name, application.Path, path, getBackups(path, pool.Name));
                })).ToList())).ToList();
    }

    public async Task<ObjectState> GetStableStateAsync(string poolName)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(options.PoolTimeoutSeconds))
        {
            using var manager = new ServerManager();
            var pool = manager.ApplicationPools[poolName]
                ?? throw new ApiException(404, "The IIS application pool was not found.");
            if (pool.State is ObjectState.Started or ObjectState.Stopped) return pool.State;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Pool '{poolName}' did not reach a stable state.");
    }

    public async Task SetStateAsync(string poolName, ObjectState desired)
    {
        var current = await GetStableStateAsync(poolName);
        if (current != desired)
        {
            using var manager = new ServerManager();
            var pool = manager.ApplicationPools[poolName]
                ?? throw new ApiException(404, "The IIS application pool was not found.");
            if (desired == ObjectState.Started) pool.Start();
            else pool.Stop();
        }

        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(options.PoolTimeoutSeconds))
        {
            using var manager = new ServerManager();
            var pool = manager.ApplicationPools[poolName];
            if (pool?.State == desired && (desired != ObjectState.Stopped ||
                !manager.WorkerProcesses.Any(process => string.Equals(process.AppPoolName, poolName,
                    StringComparison.OrdinalIgnoreCase)))) return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Pool '{poolName}' did not reach {desired} in time.");
    }

    private static string PhysicalPath(VirtualDirectory directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(directory.PhysicalPath)));
}
