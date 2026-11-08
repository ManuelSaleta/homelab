namespace VpnMediaSync.Models.Options;

public sealed class RsyncConfigOpts
{
    public const string SectionName = "Rsync";

    /// <summary>
    /// Flag determining whether rsync synchronization to the remote target host is performed.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Flag determining whether ffprobe container format validation is executed before transfer.
    /// </summary>
    public bool VerifyWithFfprobe { get; set; }

    /// <summary>
    /// Remote SSH username used for rrsync authentication on the target host.
    /// </summary>
    public string SshUser { get; set; } = string.Empty;

    /// <summary>
    /// Remote host IP address or hostname running the restricted rsync destination.
    /// </summary>
    public string SshHost { get; set; } = string.Empty;

    /// <summary>
    /// Local file system path to the private SSH key used for rrsync authentication.
    /// </summary>
    public string SshKey { get; set; } = string.Empty;

    /// <summary>
    /// Remote destination directory path for synchronized movie media files.
    /// </summary>
    public string DestMoviesDir { get; set; } = string.Empty;

    /// <summary>
    /// Remote destination directory path for synchronized television media files.
    /// </summary>
    public string DestTvDir { get; set; } = string.Empty;

    /// <summary>
    /// POSIX user and group ID ownership (e.g. '1000:1000') to apply to transferred files.
    /// </summary>
    public string Chown { get; set; } = string.Empty;

    /// <summary>
    /// Flag indicating whether local staging files should be deleted after successful remote transfer.
    /// </summary>
    public bool RemoveLocalAfterSync { get; set; }
}
