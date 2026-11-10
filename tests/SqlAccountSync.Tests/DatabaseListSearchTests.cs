using SqlAccountSync.Helpers;

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
    public void Matches_name_or_fdb(string query, string name, string path, bool expected)
    {
        Assert.Equal(expected, DatabaseListSearch.Matches(name, path, query));
    }

    [Fact]
    public void FdbFileName_takes_leaf()
    {
        Assert.Equal("ACC-ONYAT.FDB", DatabaseListSearch.FdbFileName(@"C:\eStream\SQLAccounting\DB\ACC-ONYAT.FDB"));
        Assert.Equal("", DatabaseListSearch.FdbFileName(""));
    }
}
