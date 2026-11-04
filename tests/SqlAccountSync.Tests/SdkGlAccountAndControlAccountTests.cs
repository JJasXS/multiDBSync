using SqlAccountSync.Services.SqlAccount.Sdk;

namespace SqlAccountSync.Tests;

public class SdkGlAccountAndControlAccountTests
{
    [Fact]
    public void GlAccountCodeMaxLength_IsFirebirdCharacterLength10()
    {
        Assert.Equal(10, SqlAccBizObjectWriter.GlAccountCodeMaxLength);
        Assert.Equal(10, SqlAccBizObjectWriter.ResolveGlAccountCodeMaxLength(null));
    }

    [Theory]
    [InlineData("SDKPOC1234", null)]
    [InlineData("35-000-000", null)]
    [InlineData("1234567890", null)]
    public void ValidateGlAccountCodeLength_AcceptsCodesWithinLimit(string code, string? expected)
    {
        Assert.Equal(expected, SqlAccBizObjectWriter.ValidateGlAccountCodeLength(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateGlAccountCodeLength_RequiresCode(string? code)
    {
        var err = SqlAccBizObjectWriter.ValidateGlAccountCodeLength(code);
        Assert.NotNull(err);
        Assert.Contains("required", err, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SDKPOC-A24375132")]
    [InlineData("SDKPOC-GL-2603021530121234")]
    [InlineData("12345678901")]
    public void ValidateGlAccountCodeLength_RejectsCodesOver10Chars(string code)
    {
        var err = SqlAccBizObjectWriter.ValidateGlAccountCodeLength(code);
        Assert.NotNull(err);
        Assert.Contains("10", err, StringComparison.Ordinal);
        Assert.Contains("truncation", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChooseControlAccount_PrefersExplicitMappedValue()
    {
        Assert.Equal(
            "35-000-000",
            SqlAccBizObjectWriter.ChooseControlAccount("35-000-000", "25-000-000"));
    }

    [Fact]
    public void ChooseControlAccount_UsesDestinationWhenProvidedEmpty()
    {
        Assert.Equal(
            "25-000-000",
            SqlAccBizObjectWriter.ChooseControlAccount(null, "25-000-000"));
        Assert.Equal(
            "25-000-000",
            SqlAccBizObjectWriter.ChooseControlAccount("  ", "25-000-000"));
    }

    [Fact]
    public void ChooseControlAccount_ReturnsNullWhenNothingResolvable_NoHardcodedFallback()
    {
        Assert.Null(SqlAccBizObjectWriter.ChooseControlAccount(null, null));
        Assert.Null(SqlAccBizObjectWriter.ChooseControlAccount("", ""));
        Assert.Null(SqlAccBizObjectWriter.ChooseControlAccount("  ", "  "));
    }

    [Fact]
    public void ChooseControlAccount_NeverInjectsLegacy300Or400Defaults()
    {
        var chosen = SqlAccBizObjectWriter.ChooseControlAccount(null, null);
        Assert.Null(chosen);
        Assert.NotEqual("300-000", chosen);
        Assert.NotEqual("400-000", chosen);
    }

    [Fact]
    public void ValidatePostSaveMasterPresence_FailsWhenRowMissingAfterSave()
    {
        var err = SqlAccBizObjectWriter.ValidatePostSaveMasterPresence("SDKPOC-S3517", null);
        Assert.NotNull(err);
        Assert.Contains("not found", err, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SDKPOC-S3517", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePostSaveMasterPresence_AcceptsMatchingCode()
    {
        Assert.Null(SqlAccBizObjectWriter.ValidatePostSaveMasterPresence("SDKPOC6282", "SDKPOC6282"));
        Assert.Null(SqlAccBizObjectWriter.ValidatePostSaveMasterPresence("sdkpoc6282", "SDKPOC6282"));
    }

    [Fact]
    public void ValidatePostSaveControlAccount_RequiresResolvedAndPersistedMatch()
    {
        Assert.Null(SqlAccBizObjectWriter.ValidatePostSaveControlAccount("35-000-000", "35-000-000"));
        var missing = SqlAccBizObjectWriter.ValidatePostSaveControlAccount("35-000-000", "");
        Assert.NotNull(missing);
        Assert.Contains("empty", missing, StringComparison.OrdinalIgnoreCase);
        var mismatch = SqlAccBizObjectWriter.ValidatePostSaveControlAccount("35-000-000", "400-000");
        Assert.NotNull(mismatch);
        Assert.Contains("35-000-000", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOpenedGlIdentity_DetectsDocKeyOrCodeMismatch()
    {
        Assert.Null(SqlAccBizObjectWriter.ValidateOpenedGlIdentity(687, "SDKPOC6282", 687, "SDKPOC6282"));
        var dk = SqlAccBizObjectWriter.ValidateOpenedGlIdentity(687, "SDKPOC6282", 1, "1");
        Assert.NotNull(dk);
        Assert.Contains("DOCKEY", dk, StringComparison.OrdinalIgnoreCase);
        var code = SqlAccBizObjectWriter.ValidateOpenedGlIdentity(0, "SDKPOC6282", 687, "OTHER");
        Assert.NotNull(code);
        Assert.Contains("CODE", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateGlDeleteHasNoChildren_RefusesParents()
    {
        Assert.Null(SqlAccBizObjectWriter.ValidateGlDeleteHasNoChildren("SDKPOC1834", 690, 0));
        var err = SqlAccBizObjectWriter.ValidateGlDeleteHasNoChildren("1", 684, 2);
        Assert.NotNull(err);
        Assert.Contains("child", err, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("684", err, StringComparison.Ordinal);
        Assert.DoesNotContain("Deleted", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatePostSaveControlAccount_EmptyMeansFailedPersistenceNotSuccess()
    {
        // FindKey-only "presence" with empty CA must never be treated as success.
        var err = SqlAccBizObjectWriter.ValidatePostSaveControlAccount("35-000-000", null);
        Assert.NotNull(err);
        Assert.Contains("empty", err, StringComparison.OrdinalIgnoreCase);
    }
}
