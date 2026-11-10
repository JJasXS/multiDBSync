using SqlAccountSync.Helpers;

namespace SqlAccountSync.Tests;

public class FirebirdAttachmentKickRulesTests
{
    [Fact]
    public void IdsToKick_excludes_own_and_non_positive()
    {
        var ids = FirebirdAttachmentKickRules.IdsToKick([0, -1, 5, 5, 9, 12], ownAttachmentId: 9);
        Assert.Equal([5L, 12L], ids);
    }

    [Fact]
    public void IdsToKick_empty_when_only_own()
    {
        var ids = FirebirdAttachmentKickRules.IdsToKick([3], ownAttachmentId: 3);
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
