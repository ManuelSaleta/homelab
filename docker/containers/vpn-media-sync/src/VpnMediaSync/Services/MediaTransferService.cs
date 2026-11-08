using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnMediaSync.Models;

namespace VpnMediaSync.Services;

/// <summary>
/// Transfers allowed media type (TV, Movies) from A to B. Using 'rsync'
/// </summary>
/// <param name="rsyncOptions"></param>
/// <param name="downloaderOptions"></param>
/// <param name="logger"></param>
public sealed class MediaTransferService(
    IOptions<RsyncConfigOpts> rsyncOptions,
    IOptions<DownloaderConfigOpts> downloaderOptions,
    ILogger<MediaTransferService> logger)
{
    private static readonly string[] AllowedVideoExtensions = [".mkv", ".mp4", ".mov", ".avi", ".webm"];
    private static readonly string[] AllowedMediaFormats = ["matroska", "mp4", "mov", "avi", "webm"];
    private readonly ILogger<MediaTransferService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly RsyncConfigOpts _rsyncConfig = rsyncOptions?.Value ?? throw new ArgumentNullException(nameof(rsyncOptions));
    private readonly DownloaderConfigOpts _downloaderConfig = downloaderOptions?.Value ?? throw new ArgumentNullException(nameof(downloaderOptions));

    public async Task<bool> ValidateMediaWithFfprobeAsync(string contentPath, CancellationToken ct)
    {
        if (!_rsyncConfig.VerifyWithFfprobe)
        {
            return true;
        }

        var videoFiles = GetMediaFiles(contentPath);
        if (videoFiles.Count == 0)
        {
            _logger.LogWarning("No media files found in '{ContentPath}'.", contentPath);
            return false;
        }

        foreach (var file in videoFiles)
        {
            var fi = new FileInfo(file);
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.LinkTarget != null)
            {
                _logger.LogError("Security Violation: File '{File}' is a symbolic link. ffprobe rejected.", file);
                return false;
            }

            // Run ffprobe with strict protocols, no stdin, no banners, and '--' delimiter:
            //   -v error: Suppress banners and informational output, logging only errors.
            //   -protocol_whitelist file,crypto: Block network/SSRF and subfile demuxers.
            //   -show_entries format=format_name:stream=codec_type,codec_name: Query format & stream types.
            //   -of default=noprint_wrappers=1:nokey=1: Print flat value without section wrappers or keys.
            var cmdResult = await Cli.Wrap("ffprobe")
                .WithArguments([
                    "-v", "error",
                    "-hide_banner",
                    "-protocol_whitelist", "file,crypto",
                    "-show_entries", "format=format_name:stream=codec_type,codec_name",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    "--",
                    file
                ])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            var cmdOutput = cmdResult.StandardOutput.Trim();

            if (cmdResult.ExitCode != 0)
            {
                var errorMsg = !string.IsNullOrWhiteSpace(cmdResult.StandardError)
                    ? cmdResult.StandardError.Trim()
                    : cmdOutput;

                _logger.LogError(
                    "ffprobe validation rejected '{File}' (exit={ExitCode}, error='{Error}').",
                    file,
                    cmdResult.ExitCode,
                    errorMsg);
                return false;
            }

            var foundAllowedFormat = AllowedMediaFormats.Any(allowed => cmdOutput.Contains(allowed, StringComparison.OrdinalIgnoreCase));
            var hasVideoStream = cmdOutput.Contains("video", StringComparison.OrdinalIgnoreCase);

            if (!foundAllowedFormat || !hasVideoStream)
            {
                _logger.LogError(
                    "ffprobe validation rejected '{File}': output '{Format}' lacks allowed container format or video stream.",
                    file,
                    cmdOutput);
                return false;
            }
        }

        return true;
    }

    public async Task<bool> TransferAsync(QBitTorrentItem torrent, CancellationToken ct)
    {
        if (!_rsyncConfig.Enabled)
        {
            _logger.LogInformation("Rsync is disabled in config.json; skipping transfer for '{Name}'.", torrent.Name);
            return false;
        }

        var rawSourcePath = !string.IsNullOrWhiteSpace(torrent.ContentPath)
            ? torrent.ContentPath
            : Path.Combine(torrent.SavePath ?? _downloaderConfig.StagingDir, torrent.Name);

        var sourcePath = ResolveLocalSourcePath(rawSourcePath);

        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            _logger.LogWarning("Completed torrent path '{SourcePath}' does not exist locally (resolved: '{ResolvedPath}').", rawSourcePath, sourcePath);
            return false;
        }

        if (!await ValidateMediaWithFfprobeAsync(sourcePath, ct))
        {
            return false;
        }

        var isTv = string.Equals(torrent.Category, "tv", StringComparison.OrdinalIgnoreCase);
        var remoteBaseDir = isTv ? _rsyncConfig.DestTvDir : _rsyncConfig.DestMoviesDir;

        // When connecting to micro-nas with a restricted rrsync key (e.g. command="rrsync -wo /mnt/export/storage/plex/media"),
        // the server-side rrsync root is already at /mnt/export/storage/plex/media.
        // Strip the redundant prefix if present so destination evaluates cleanly to "Movies/" or "TV Shows/".
        const string rrsyncPrefix = "/mnt/export/storage/plex/media";
        var relativeDir = remoteBaseDir.StartsWith(rrsyncPrefix, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(rrsyncPrefix, remoteBaseDir)
            : remoteBaseDir;

        if (string.IsNullOrWhiteSpace(relativeDir) || relativeDir == ".")
        {
            relativeDir = isTv ? "TV Shows" : "Movies";
        }

        var destination = $"{_rsyncConfig.SshUser}@{_rsyncConfig.SshHost}:{relativeDir.TrimEnd('/')}/";

        var sshCmd = $"ssh -i {_rsyncConfig.SshKey} -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10";

        // Hardened Rsync Arguments matching Micro-NAS storage permissions (1000:1000, 775/664):
        //   -rtvP: Recursive, timestamps, verbose, partial (avoids -a which copies symlinks & device nodes)
        //   --no-links / --no-devices / --no-specials: Reject syncing symlinks and character/block/FIFO device nodes
        //   --safe-links: Ignore links pointing outside destination
        //   --prune-empty-dirs: Excludes directory shells that lack verified media files
        // Note: --protect-args (-s) is omitted because rrsync disables -s so it can validate paths in SSH_ORIGINAL_COMMAND
        var args = new List<string>
        {
            "-rtvP",
            "--partial",
            "--no-perms",
            "--no-links",
            "--no-devices",
            "--no-specials",
            "--safe-links",
            "--prune-empty-dirs",
            $"--chown={_rsyncConfig.Chown}",
            "--chmod=Du=rwx,Dg=rwx,Do=rx,Fu=rw,Fg=rw,Fo=r",
            "--include=*/"
        };

        foreach (var ext in AllowedVideoExtensions)
        {
            args.Add($"--include=*{ext}");
        }

        var srtFiles = GetValidatedSubtitleFiles(sourcePath);
        if (srtFiles.Count > 0)
        {
            args.Add("--include=*.srt");
        }

        args.Add("--exclude=*");
        args.Add("-e");
        args.Add(sshCmd);
        args.Add("--");
        args.Add(sourcePath);
        args.Add(destination);

        _logger.LogInformation(
            "Starting rsync transfer for '{Name}' -> '{Destination}'.",
            torrent.Name,
            destination);

        var result = await Cli.Wrap("rsync")
            .WithArguments(args)
            .WithValidation(CommandResultValidation.ZeroExitCode)
            .ExecuteBufferedAsync(ct);

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Rsync failed for '{Name}' (exit={ExitCode}): {Stderr}",
                torrent.Name,
                result.ExitCode,
                result.StandardError.Trim());
            return false;
        }

        _logger.LogInformation("Successfully synced '{Name}' to {Host}.", torrent.Name, _rsyncConfig.SshHost);
        return true;
    }

    public void CleanupLocalStaging(QBitTorrentItem torrent)
    {
        CleanupLocalStagingAsync(torrent, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task CleanupLocalStagingAsync(QBitTorrentItem torrent, CancellationToken ct = default)
    {
        var rawPath = torrent.ContentPath ?? torrent.SavePath;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        string sourcePath;
        try
        {
            sourcePath = ResolveLocalSourcePath(rawPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve local staging path '{RawPath}' for cleanup.", rawPath);
            return;
        }

        // Allow a brief settle window for qBittorrent to complete its background file deletion if deleteFiles was requested
        for (var i = 0; i < 5; i++)
        {
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                _logger.LogDebug("Local staging path '{SourcePath}' already removed.", sourcePath);
                return;
            }

            try
            {
                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                return;
            }

            try
            {
                if (File.Exists(sourcePath))
                {
                    File.SetAttributes(sourcePath, FileAttributes.Normal);
                    File.Delete(sourcePath);
                    _logger.LogInformation("Cleaned up local staging file '{SourcePath}'.", sourcePath);
                    return;
                }

                if (Directory.Exists(sourcePath))
                {
                    ResetFileAttributes(new DirectoryInfo(sourcePath));
                    Directory.Delete(sourcePath, recursive: true);
                    _logger.LogInformation("Cleaned up local staging directory '{SourcePath}'.", sourcePath);
                    return;
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
            {
                // Removed concurrently (e.g. by qBittorrent)
                _logger.LogDebug("Local staging path '{SourcePath}' was removed concurrently.", sourcePath);
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogDebug(ex, "Transient error cleaning up '{SourcePath}' (attempt {Attempt}/3), retrying...", sourcePath, attempt);
                try
                {
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                // If it was removed during our attempts, do not warn
                if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                {
                    return;
                }

                _logger.LogWarning(ex, "Failed to clean up local staging path '{RawPath}'.", rawPath);
            }
        }
    }

    private static void ResetFileAttributes(DirectoryInfo directory)
    {
        try
        {
            directory.Attributes = FileAttributes.Normal;
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    file.Attributes = FileAttributes.Normal;
                }
                catch
                {
                    // Ignore individual attribute reset failures
                }
            }
        }
        catch
        {
            // Directory may have been removed concurrently
        }
    }

    private string ResolveLocalSourcePath(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(remotePath));
        }

        var localStaging = Path.GetFullPath(_downloaderConfig.StagingDir).TrimEnd(Path.DirectorySeparatorChar);
        var remotePrefix = (_downloaderConfig.RemotePathPrefix ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar);

        string candidatePath;
        if (!string.IsNullOrWhiteSpace(remotePrefix) && remotePath.StartsWith(remotePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(remotePrefix, remotePath);
            candidatePath = Path.GetFullPath(Path.Combine(localStaging, relative));
        }
        else if (Path.IsPathRooted(remotePath))
        {
            candidatePath = Path.GetFullPath(remotePath);
        }
        else
        {
            candidatePath = Path.GetFullPath(Path.Combine(localStaging, remotePath));
        }

        // STRICT STAGING BOUNDARY ENFORCEMENT:
        // candidatePath MUST reside strictly inside localStaging
        var stagingPrefixWithSep = localStaging + Path.DirectorySeparatorChar;
        if (!candidatePath.StartsWith(stagingPrefixWithSep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidatePath, localStaging, StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.SecurityException(
                $"CRITICAL SECURITY VIOLATION: Path '{remotePath}' resolved to '{candidatePath}' outside allowed staging root '{localStaging}'.");
        }

        // Guard against symlinks
        if (File.Exists(candidatePath))
        {
            var fi = new FileInfo(candidatePath);
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.LinkTarget != null)
            {
                throw new System.Security.SecurityException(
                    $"CRITICAL SECURITY VIOLATION: File '{candidatePath}' is a symbolic link or reparse point.");
            }
        }
        else if (Directory.Exists(candidatePath))
        {
            var di = new DirectoryInfo(candidatePath);
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0 || di.LinkTarget != null)
            {
                throw new System.Security.SecurityException(
                    $"CRITICAL SECURITY VIOLATION: Directory '{candidatePath}' is a symbolic link or reparse point.");
            }
        }

        return candidatePath;
    }

    private static List<string> GetMediaFiles(string path)
    {
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path);
            var fi = new FileInfo(path);
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.LinkTarget != null)
            {
                return [];
            }

            return AllowedVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                ? [path]
                : [];
        }

        if (Directory.Exists(path))
        {
            return [.. Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    var ext = Path.GetExtension(f);
                    var fi = new FileInfo(f);

                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.LinkTarget != null)
                    {
                        return false;
                    }

                    return !name.Contains("sample", StringComparison.OrdinalIgnoreCase) &&
                           AllowedVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
                })];
        }

        return [];
    }

    private List<string> GetValidatedSubtitleFiles(string path)
    {
        var result = new List<string>();
        IEnumerable<string> candidates;

        if (File.Exists(path))
        {
            if (string.Equals(Path.GetExtension(path), ".srt", StringComparison.OrdinalIgnoreCase))
            {
                candidates = [path];
            }
            else
            {
                var srtSibling = Path.ChangeExtension(path, ".srt");
                candidates = File.Exists(srtSibling) ? [srtSibling] : [];
            }
        }
        else if (Directory.Exists(path))
        {
            candidates = Directory.EnumerateFiles(path, "*.srt", SearchOption.AllDirectories);
        }
        else
        {
            return [];
        }

        foreach (var file in candidates)
        {
            if (IsValidSubtitleFile(file))
            {
                result.Add(file);
            }
            else
            {
                _logger.LogWarning("Skipping unverified or non-compliant subtitle file '{File}'.", file);
            }
        }

        return result;
    }

    private static bool IsValidSubtitleFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || (fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.LinkTarget != null)
            {
                return false;
            }

            // Subtitle size ceiling: maximum 2MB
            if (fi.Length > 2 * 1024 * 1024 || fi.Length == 0)
            {
                return false;
            }

            // Verify plain UTF-8 / ASCII text (no binary null bytes or script injections)
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(4096, (int)fi.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    return false;
                }
            }

            var preview = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            if (preview.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
                preview.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
                preview.StartsWith("MZ", StringComparison.Ordinal) ||
                preview.StartsWith("\x7fELF", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
