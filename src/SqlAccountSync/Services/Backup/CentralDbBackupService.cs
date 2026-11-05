using System.IO.Compression;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.Backup;

public sealed record CentralDbBackupResult(byte[] ZipBytes, string DownloadFileName, string SourcePath);

public interface ICentralDbBackupService
{
    bool IsConfigured { get; }
    string? ConfiguredPath { get; }
    Task<CentralDbBackupResult> CreateZipAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Copies the central SYNC.FDB (with shared read) into a zip for download.
/// Hot copy under load may be incomplete — prefer quiet periods or Firebird gbak for formal DR.
/// </summary>
public sealed class CentralDbBackupService : ICentralDbBackupService
{
    private readonly CentralDbOptions _options;
    private readonly ILogger<CentralDbBackupService> _logger;

    public CentralDbBackupService(IOptions<CentralDbOptions> options, ILogger<CentralDbBackupService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Path) && File.Exists(NormalizePath(_options.Path));

    public string? ConfiguredPath =>
        string.IsNullOrWhiteSpace(_options.Path) ? null : NormalizePath(_options.Path);

    public async Task<CentralDbBackupResult> CreateZipAsync(CancellationToken cancellationToken = default)
    {
        var source = ConfiguredPath
            ?? throw new InvalidOperationException("CentralDb:Path is not configured.");
        if (!File.Exists(source))
            throw new FileNotFoundException("Central SYNC.FDB was not found.", source);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var entryName = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(entryName))
            entryName = "SYNC.FDB";

        var workRoot = Path.Combine(Path.GetTempPath(), "SqlAccountSync-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            var copyPath = Path.Combine(workRoot, entryName);
            await CopySharedAsync(source, copyPath, cancellationToken).ConfigureAwait(false);

            await using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                archive.CreateEntryFromFile(copyPath, entryName, CompressionLevel.Optimal);
            }

            var bytes = zipStream.ToArray();
            var downloadName = Path.GetFileNameWithoutExtension(entryName) + "-backup-" + stamp + ".zip";
            _logger.LogInformation(
                "Central DB backup zip created ({Bytes} bytes) from {Path}",
                bytes.Length, source);
            return new CentralDbBackupResult(bytes, downloadName, source);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot))
                    Directory.Delete(workRoot, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed cleaning backup temp folder {Dir}", workRoot);
            }
        }
    }

    private static string NormalizePath(string path) =>
        path.Trim().Replace('/', Path.DirectorySeparatorChar);

    private static async Task CopySharedAsync(string source, string dest, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 64,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            dest,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 64,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
