using SqlAccountSync.Helpers;
using SqlAccountSync.Models;

namespace SqlAccountSync.Tests;

public class DatabaseListSearchTests
{
    [Theory]
    [InlineData("On Yat", "On Yat Database", @"C:/DB/ACC-ONYAT.FDB", true)]
    [InlineData("onyat", "On Yat Database", @"C:/DB/ACC-ONYAT.FDB", true)]
    [InlineData("ACC-ONYAT", "On Yat Database", @"C:/DB/ACC-ONYAT.FDB", true)]
    [InlineData(".fdb", "On Yat Database", @"C:/DB/ACC-ONYAT.FDB", true)]
    [InlineData("missing", "On Yat Database", @"C:/DB/ACC-ONYAT.FDB", false)]
    [InlineData("", "On Yat Database", @"C:/DB/ACC-ONYAT.FDB", true)]
    [InlineData("  ", "On Yat Database", @"C:/DB/ACC-ONYAT.FDB", true)]
    public void MatchesText_name_or_fdb(string query, string name, string path, bool expected)
    {
        Assert.Equal(expected, DatabaseListSearch.MatchesText(name, path, query));
    }

    [Theory]
    [InlineData(DatabaseRoles.Master, "ALL", true)]
    [InlineData(DatabaseRoles.Sub, "ALL", true)]
    [InlineData(DatabaseRoles.Master, "MASTER", true)]
    [InlineData(DatabaseRoles.Both, "MASTER", true)]
    [InlineData(DatabaseRoles.Sub, "MASTER", false)]
    [InlineData(DatabaseRoles.Sub, "SUB", true)]
    [InlineData(DatabaseRoles.Both, "SUB", true)]
    [InlineData(DatabaseRoles.Master, "SUB", false)]
    public void MatchesRole_master_or_sub(string role, string filter, bool expected)
    {
        Assert.Equal(expected, DatabaseListSearch.MatchesRole(role, filter));
    }

    [Fact]
    public void Matches_combines_text_and_role()
    {
        Assert.True(DatabaseListSearch.Matches("On Yat", @"C:/DB/ACC-ONYAT.FDB", DatabaseRoles.Sub, "onyat", "SUB"));
        Assert.False(DatabaseListSearch.Matches("On Yat", @"C:/DB/ACC-ONYAT.FDB", DatabaseRoles.Sub, "onyat", "MASTER"));
        Assert.False(DatabaseListSearch.Matches("On Yat", @"C:/DB/ACC-ONYAT.FDB", DatabaseRoles.Sub, "zzz", "SUB"));
    }

    [Fact]
    public void FdbFileName_takes_leaf()
    {
        Assert.Equal("ACC-ONYAT.FDB", DatabaseListSearch.FdbFileName(@"C:\eStream\SQLAccounting\DB\ACC-ONYAT.FDB"));
        Assert.Equal("", DatabaseListSearch.FdbFileName(""));
    }
}
