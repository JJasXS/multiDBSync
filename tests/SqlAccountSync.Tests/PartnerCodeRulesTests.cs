using SqlAccountSync.Helpers;

namespace SqlAccountSync.Tests;

public class PartnerCodeRulesTests
{
    [Theory]
    [InlineData("300-C0001")]
    [InlineData("400-H0001")]
    [InlineData("SYNC-GR-SU")]
    [InlineData("A")]
    public void Validate_accepts_valid_codes(string code)
    {
        var (ok, msg) = PartnerCodeRules.Validate(code);
        Assert.True(ok, msg);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TOO-LONG-CODE")]
    [InlineData("300 C0001")]
    [InlineData("300_C0001")]
    public void Validate_rejects_invalid_codes(string code)
    {
        var (ok, _) = PartnerCodeRules.Validate(code);
        Assert.False(ok);
    }

    [Theory]
    [InlineData("John Trading", 'J')]
    [InlineData("  john", 'J')]
    [InlineData("123 Corp", '1')]
    [InlineData("---", 'X')]
    [InlineData("", 'X')]
    [InlineData(null, 'X')]
    public void LetterFromCompanyName_matches_eQuotation(string? name, char expected)
    {
        Assert.Equal(expected, PartnerCodeRules.LetterFromCompanyName(name));
    }

    [Theory]
    [InlineData("CUSTOMER", "John Trading", new[] { "300-C0005", "300-J0002", "300-H0004" }, "300-J0003")]
    [InlineData("SUPPLIER", "John Trading", new[] { "400-S0005", "400-J0001", "400-H0002" }, "400-J0002")]
    [InlineData("CUSTOMER", "John Trading", new string[0], "300-J0001")]
    [InlineData("SUPPLIER", "Acme", new string[0], "400-A0001")]
    [InlineData("CUSTOMER", "", new string[0], "300-X0001")]
    [InlineData("SUPPLIER", "!!!", new string[0], "400-X0001")]
    [InlineData("CUSTOMER", "123 Corp", new[] { "300-10001" }, "300-10002")]
    [InlineData("CUSTOMER", "John", new[] { "300-J0001", "300-C9999" }, "300-J0002")]
    public void SuggestNext_uses_company_name_letter_series(
        string entity, string companyName, string[] existing, string expected)
    {
        Assert.Equal(expected, PartnerCodeRules.SuggestNext(entity, existing, companyName));
    }
}
