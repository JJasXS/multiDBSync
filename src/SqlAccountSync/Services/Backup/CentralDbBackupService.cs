using System.IO.Compression;
using Microsoft.Extensions.Options;
using SqlAccountSync.Options;

namespace SqlAccountSync.Services.Backup;

public sealed record CentralDbBackupResult(
    string SavedZipPath,
    string FileName,
    string SourcePath,
    long ByteLength);

public interface ICentralDbBackupService
{
    bool IsConfigured { get; }
    string? ConfiguredPath { get; }
    IReadOnlyList<CentralDbBackupTarget> ListTargets();
    Task<CentralDbBackupResult> SaveZipAsync(string targetId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Copies the central SYNC.FDB (shared read), zips it, and writes under a chosen server backup folder.
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

    public IReadOnlyList<CentralDbBackupTarget> ListTargets() =>
        CentralDbBackupTargets.Resolve(ConfiguredPath);

    public async Task<CentralDbBackupResult> SaveZipAsync(
        string targetId, CancellationToken cancellationToken = default)
    {
        var source = ConfiguredPath
            ?? throw new InvalidOperationException("CentralDb:Path is not configured.");
        if (!File.Exists(source))
            throw new FileNotFoundException("Central SYNC.FDB was not found.", source);

        var destDir = CentralDbBackupTargets.ResolvePath(targetId, source)
            ?? throw new InvalidOperationException("Unknown backup folder selection.");
        destDir = NormalizePath(destDir);
        Directory.CreateDirectory(destDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var entryName = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(entryName))
            entryName = "SYNC.FDB";

        var fileName = Path.GetFileNameWithoutExtension(entryName) + "-backup-" + stamp + ".zip";
        var zipPath = Path.Combine(destDir, fileName);
        if (File.Exists(zipPath))
            throw new IOException("Backup file already exists: " + zipPath);

        var workRoot = Path.Combine(Path.GetTempPath(), "SqlAccountSync-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        try
        {
            var copyPath = Path.Combine(workRoot, entryName);
            await CopySharedAsync(source, copyPath, cancellationToken).ConfigureAwait(false);

            await using (var zipStream = new FileStream(
                zipPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 64,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(copyPath, entryName, CompressionLevel.Optimal);
            }

            var length = new FileInfo(zipPath).Length;
            _logger.LogInformation(
                "Central DB backup saved ({Bytes} bytes) from {Source} to {Zip}",
                length, source, zipPath);
            return new CentralDbBackupResult(zipPath, fileName, source, length);
        }
        catch
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
            catch
            {
                // ignore cleanup
            }

            throw;
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
