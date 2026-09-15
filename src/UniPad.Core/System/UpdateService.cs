using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Serilog;

namespace UniPad.Core.SystemServices;

/// <summary>Outcome of an update check or install attempt.</summary>
public enum UpdateStatus
{
    /// <summary>The running build is the newest published release.</summary>
    UpToDate,
    /// <summary>A newer release exists and can be downloaded.</summary>
    UpdateAvailable,
    /// <summary>The new executable is staged; a restart completes the update.</summary>
    Installed,
    /// <summary>The check or the download failed.</summary>
    Failed,
}

/// <summary>Result of a single update operation.</summary>
/// <param name="Status">What happened.</param>
/// <param name="CurrentVersion">Version of the running build.</param>
/// <param name="LatestVersion">Version published on GitHub, when known.</param>
/// <param name="DownloadUrl">Direct asset URL of the new executable, when one exists.</param>
/// <param name="ReleaseUrl">Human-facing release page, used as a manual fallback.</param>
/// <param name="Message">Detail for the status bar; empty on success paths.</param>
public readonly record struct UpdateCheckResult(
    UpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    string? DownloadUrl,
    string? ReleaseUrl,
    string Message);

/// <summary>
/// Checks GitHub Releases for a newer UniPad build and replaces the running executable with it.
/// <para>
/// Windows keeps a running image locked against writing but not against renaming, so the update is
/// applied by moving the current exe aside to <c>UniPad.exe.old</c> and moving the download into
/// its place. The stale backup is deleted on the next start by <see cref="CleanupPreviousUpdate"/>.
/// Nothing is ever overwritten before the download has been fully verified, so a failed or
/// truncated transfer leaves the installation exactly as it was.
/// </para>
/// </summary>
public static class UpdateService
{
    /// <summary>Repository that publishes the releases.</summary>
    public const string Repository = "UniPad-app/UniPad";

    /// <summary>Latest non-draft, non-prerelease release of the repository.</summary>
    public const string LatestReleaseApi = $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>Release listing page, offered when an automatic update is not possible.</summary>
    public const string ReleasesPage = $"https://github.com/{Repository}/releases/latest";

    /// <summary>A portable UniPad build is a few megabytes; anything smaller is not the real asset.</summary>
    private const long MinimumAssetBytes = 1_000_000;

    private const string BackupSuffix = ".old";
    private const string StageFileName = "UniPad.update.tmp";

    /// <summary>Informational version of the running build, e.g. <c>1.1.0</c>.</summary>
    public static string CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational.Split('+')[0];
            }

            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>
    /// Deletes the backup left behind by a previous update. Called once at startup, when the new
    /// build is the running image and the old one is no longer locked.
    /// </summary>
    public static void CleanupPreviousUpdate()
    {
        try
        {
            var backup = PortablePaths.ExecutablePath + BackupSuffix;
            if (File.Exists(backup))
            {
                File.Delete(backup);
                Log.Information("Removed the previous executable left by an update");
            }

            var stage = Path.Combine(PortablePaths.ExecutableDirectory, StageFileName);
            if (File.Exists(stage))
            {
                File.Delete(stage);
            }
        }
        catch (Exception ex)
        {
            // A leftover file costs nothing but disk space, so never fail startup over it.
            Log.Debug(ex, "Could not clean up after a previous update");
        }
    }

    /// <summary>Asks GitHub whether a newer release exists.</summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion;

        try
        {
            using var client = CreateClient();

            using var response = await client
                .GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 means the repository has no published release yet, which is not an error the
                // user can act on, so it is reported as "nothing newer" rather than as a failure.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new UpdateCheckResult(
                        UpdateStatus.UpToDate, current, null, null, ReleasesPage,
                        "No published release was found for this repository.");
                }

                return new UpdateCheckResult(
                    UpdateStatus.Failed, current, null, null, ReleasesPage,
                    $"GitHub replied with {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // JsonDocument is used instead of a DTO on purpose: it needs no reflection and no
            // source-generated context, so it keeps working under trimming and single-file publish.
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var tag = ReadString(root, "tag_name");
            var releaseUrl = ReadString(root, "html_url") ?? ReleasesPage;
            var latest = ParseVersion(tag);

            if (latest == EmptyVersion)
            {
                return new UpdateCheckResult(
                    UpdateStatus.Failed, current, tag, null, releaseUrl,
                    $"Could not read a version number from the release tag '{tag}'.");
            }

            var (assetUrl, assetSize) = FindExecutableAsset(root);
            var latestText = FormatVersion(latest);

            if (latest <= ParseVersion(current))
            {
                return new UpdateCheckResult(
                    UpdateStatus.UpToDate, current, latestText, assetUrl, releaseUrl, string.Empty);
            }

            if (assetUrl is null || assetSize is > 0 and < MinimumAssetBytes)
            {
                return new UpdateCheckResult(
                    UpdateStatus.Failed, current, latestText, null, releaseUrl,
                    $"Release {latestText} carries no UniPad.exe asset; please download it manually.");
            }

            return new UpdateCheckResult(
                UpdateStatus.UpdateAvailable, current, latestText, assetUrl, releaseUrl, string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            return new UpdateCheckResult(
                UpdateStatus.Failed, current, null, null, ReleasesPage, ex.Message);
        }
    }

    /// <summary>
    /// Downloads the new executable and swaps it in. The running process keeps working normally
    /// until it is restarted.
    /// </summary>
    /// <param name="candidate">A result whose status is <see cref="UpdateStatus.UpdateAvailable"/>.</param>
    /// <param name="progress">Receives download completion in percent.</param>
    public static async Task<UpdateCheckResult> DownloadAndInstallAsync(
        UpdateCheckResult candidate,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (candidate.DownloadUrl is not { Length: > 0 } url)
        {
            return candidate with
            {
                Status = UpdateStatus.Failed,
                Message = "No download URL was resolved for this release.",
            };
        }

        var exePath = PortablePaths.ExecutablePath;
        var directory = PortablePaths.ExecutableDirectory;

        // Staging next to the exe guarantees the same volume, so the final swap is an atomic
        // rename rather than a copy that could be interrupted half-written.
        var stagePath = Path.Combine(directory, StageFileName);
        var backupPath = exePath + BackupSuffix;

        try
        {
            await DownloadAsync(url, stagePath, progress, cancellationToken).ConfigureAwait(false);

            var info = new FileInfo(stagePath);
            if (info.Length < MinimumAssetBytes || !IsWindowsExecutable(stagePath))
            {
                TryDelete(stagePath);
                return candidate with
                {
                    Status = UpdateStatus.Failed,
                    Message = "The download did not look like a valid UniPad executable; nothing was changed.",
                };
            }

            TryDelete(backupPath);
            File.Move(exePath, backupPath);

            try
            {
                File.Move(stagePath, exePath);
            }
            catch
            {
                // Put the working build back before surfacing the failure.
                File.Move(backupPath, exePath);
                throw;
            }

            Log.Information("Updated UniPad from {Old} to {New}", candidate.CurrentVersion, candidate.LatestVersion);

            return candidate with { Status = UpdateStatus.Installed, Message = string.Empty };
        }
        catch (OperationCanceledException)
        {
            TryDelete(stagePath);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(stagePath);
            Log.Warning(ex, "Update install was denied by the file system");

            return candidate with
            {
                Status = UpdateStatus.Failed,
                Message = $"'{directory}' is not writable. Move UniPad to a normal folder or update it manually.",
            };
        }
        catch (Exception ex)
        {
            TryDelete(stagePath);
            Log.Error(ex, "Update install failed");
            return candidate with { Status = UpdateStatus.Failed, Message = ex.Message };
        }
    }

    /// <summary>Launches the freshly installed build. The caller is expected to exit afterwards.</summary>
    public static bool TryRestart() => DriverBootstrapper.TryRestartApplication();

    /// <summary>Opens the release page in the default browser.</summary>
    public static void OpenReleasePage(string? url = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url is { Length: > 0 } ? url : ReleasesPage,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not open the release page");
        }
    }

    private static async Task DownloadAsync(
        string url,
        string destination,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();

        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0L;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81_920];
        long written = 0;
        var lastReported = -1;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;

            if (total <= 0 || progress is null)
            {
                continue;
            }

            // Reporting every chunk would flood the UI thread with dispatcher posts.
            var percent = (int)(written * 100 / total);
            if (percent != lastReported)
            {
                lastReported = percent;
                progress.Report(percent);
            }
        }
    }

    private static (string? Url, long Size) FindExecutableAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, 0);
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = ReadString(asset, "name");
            if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip installers or side artefacts that happen to be executables.
            if (!name.Contains("UniPad", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value)
                ? value
                : 0L;

            return (ReadString(asset, "browser_download_url"), size);
        }

        return (null, 0);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"UniPad/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Confirms the download starts with the <c>MZ</c> signature of a Windows binary.</summary>
    private static bool IsWindowsExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    private static readonly Version EmptyVersion = new(0, 0, 0, 0);

    /// <summary>Turns <c>v1.2.0</c>, <c>1.2.0-beta1</c> or <c>1.2</c> into a comparable version.</summary>
    private static Version ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return EmptyVersion;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        var cut = trimmed.IndexOfAny(['-', '+', ' ']);
        if (cut > 0)
        {
            trimmed = trimmed[..cut];
        }

        if (!Version.TryParse(trimmed, out var parsed))
        {
            return EmptyVersion;
        }

        return new Version(
            parsed.Major,
            parsed.Minor,
            parsed.Build < 0 ? 0 : parsed.Build,
            parsed.Revision < 0 ? 0 : parsed.Revision);
    }

    private static string FormatVersion(Version version) =>
        version.Revision > 0 ? version.ToString(4) : version.ToString(3);

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Nothing useful can be done; the caller reports the real failure.
        }
    }
}
