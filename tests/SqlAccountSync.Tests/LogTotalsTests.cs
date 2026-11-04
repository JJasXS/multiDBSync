using SqlAccountSync.Pages;

namespace SqlAccountSync.Tests;

public class LogTotalsTests
{
    [Fact]
    public void All_Is_Sum_Of_Category_Totals()
    {
        var totals = new LogTotals(Transaction: 1, Audit: 39, Error: 0);
        Assert.Equal(40, totals.All);
    }

    [Fact]
    public void Zero_Errors_Does_Not_Inflate_All()
    {
        var totals = new LogTotals(Transaction: 12, Audit: 100, Error: 0);
        Assert.Equal(0, totals.Error);
        Assert.Equal(112, totals.All);
    }
}
