using SqlAccountSync.Helpers;

namespace SqlAccountSync.Tests;

public class FirebirdAttachmentKickRulesTests
{
    [Fact]
    public void IdsToKick_excludes_own_system_and_non_positive()
    {
        var ids = FirebirdAttachmentKickRules.IdsToKick(
            [
                (0, "SYSDBA"),
                (-1, "SYSDBA"),
                (5, "SYSDBA"),
                (5, "SYSDBA"),
                (9, "SYSDBA"),
                (12, "SYSDBA"),
                (8, "Cache Writer"),
                (7, "Garbage Collector"),
            ],
            ownAttachmentId: 9);
        Assert.Equal([5L, 12L], ids);
    }

    [Fact]
    public void IdsToKick_empty_when_only_own()
    {
        var ids = FirebirdAttachmentKickRules.IdsToKick([(3, "SYSDBA")], ownAttachmentId: 3);
        Assert.Empty(ids);
    }

    [Theory]
    [InlineData(@"C:\SQL Accounting\SQLACC.EXE", "SQLACC.EXE")]
    [InlineData("/opt/app/SqlAccountSync.dll", "SqlAccountSync.dll")]
    [InlineData("", "—")]
    [InlineData(null, "—")]
    public void ShortProcess_uses_file_name(string? path, string expected)
    {
        Assert.Equal(expected, FirebirdAttachmentKickRules.ShortProcess(path));
    }

    [Theory]
    [InlineData(@"C:\SQL Accounting\SQLACC.EXE", true)]
    [InlineData("sqlacc.exe", true)]
    [InlineData("SQLACC", true)]
    [InlineData("SqlAccountSync.dll", false)]
    [InlineData(null, false)]
    public void LooksLikeSqlAcc_detects_sqlacc(string? path, bool expected)
    {
        Assert.Equal(expected, FirebirdAttachmentKickRules.LooksLikeSqlAcc(path));
    }
}
