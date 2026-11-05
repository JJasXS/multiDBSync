using Microsoft.Extensions.Logging.Abstractions;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Backup;
using System.IO.Compression;

namespace SqlAccountSync.Tests;

public class CentralDbBackupServiceTests
{
    [Fact]
    public async Task CreateZipAsync_copies_and_zips_fdb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sas-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fdb = Path.Combine(dir, "SYNC.FDB");
            await File.WriteAllBytesAsync(fdb, "fake-sync-fdb-bytes"u8.ToArray());

            var svc = new CentralDbBackupService(
                Microsoft.Extensions.Options.Options.Create(new CentralDbOptions { Path = fdb, Host = "localhost" }),
                NullLogger<CentralDbBackupService>.Instance);

            Assert.True(svc.IsConfigured);
            var result = await svc.CreateZipAsync();
            Assert.True(result.ZipBytes.Length > 0);
            Assert.EndsWith(".zip", result.DownloadFileName, StringComparison.OrdinalIgnoreCase);

            await using var ms = new MemoryStream(result.ZipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = Assert.Single(zip.Entries);
            Assert.Equal("SYNC.FDB", entry.Name);
            await using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream);
            Assert.Equal("fake-sync-fdb-bytes", await reader.ReadToEndAsync());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void IsConfigured_false_when_missing_file()
    {
        var svc = new CentralDbBackupService(
            Microsoft.Extensions.Options.Options.Create(new CentralDbOptions
            {
                Path = Path.Combine(Path.GetTempPath(), "no-such-sync-" + Guid.NewGuid().ToString("N") + ".FDB"),
                Host = "localhost"
            }),
            NullLogger<CentralDbBackupService>.Instance);

        Assert.False(svc.IsConfigured);
    }
}
