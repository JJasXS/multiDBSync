using Microsoft.Extensions.Logging.Abstractions;
using SqlAccountSync.Options;
using SqlAccountSync.Services.Backup;
using System.IO.Compression;

namespace SqlAccountSync.Tests;

public class CentralDbBackupServiceTests
{
    [Fact]
    public async Task SaveZipAsync_writes_zip_under_chosen_folder()
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

            // Use beside-fdb so the test does not write under C:\eStream.
            var result = await svc.SaveZipAsync(CentralDbBackupTargets.BesideFdbId);
            Assert.True(File.Exists(result.SavedZipPath));
            Assert.Equal(Path.Combine(dir, "backup"), Path.GetDirectoryName(result.SavedZipPath),
                ignoreCase: true);
            Assert.EndsWith(".zip", result.FileName, StringComparison.OrdinalIgnoreCase);

            await using var zipStream = File.OpenRead(result.SavedZipPath);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
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
    public void Targets_default_is_estream_backup()
    {
        var targets = CentralDbBackupTargets.Resolve(@"C:\SQLData\SqlAccountSync\SYNC.FDB");
        Assert.Equal(3, targets.Count);
        Assert.Equal(CentralDbBackupTargets.EStreamId, CentralDbBackupTargets.DefaultId);
        Assert.Equal(@"C:\eStream\SQLAccounting\Backup", targets[0].Path);
        Assert.Equal(@"C:\Apps\SqlAccountSync\backup", targets[1].Path);
        Assert.Equal(@"C:\SQLData\SqlAccountSync\backup", targets[2].Path);
        Assert.Equal(@"C:\eStream\SQLAccounting\Backup",
            CentralDbBackupTargets.ResolvePath(null, @"C:\SQLData\SqlAccountSync\SYNC.FDB"));
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
