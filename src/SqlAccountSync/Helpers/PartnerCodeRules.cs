using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlAccountSync.Helpers;

/// <summary>SQL Accounting partner (customer/supplier) code rules.</summary>
public static partial class PartnerCodeRules
{
    /// <summary>Max length observed for AR_CUSTOMER / AP_SUPPLIER CODE (API truncates longer values).</summary>
    public const int MaxLength = 10;

    public static (bool Ok, string Message) Validate(string? code)
    {
        var c = (code ?? "").Trim();
        if (c.Length == 0)
            return (false, "Code is required.");
        if (c.Length > MaxLength)
            return (false, $"Code must be at most {MaxLength} characters (SQL Accounting limit).");
        if (!CodePattern().IsMatch(c))
            return (false, "Code may only contain letters, digits, and hyphens.");
        return (true, "");
    }

    /// <summary>
    /// First alphanumeric character of the company name (uppercase), or 'X' if none — matches eQuotation guest codes.
    /// </summary>
    public static char LetterFromCompanyName(string? companyName)
    {
        foreach (var ch in companyName ?? "")
        {
            if (char.IsAsciiLetterOrDigit(ch))
                return char.ToUpperInvariant(ch);
        }
        return 'X';
    }

    /// <summary>
    /// Suggests the next code like 300-J0001 / 400-J0001 from company name letter + existing codes in that series.
    /// </summary>
    public static string SuggestNext(string entityCode, IEnumerable<string> existingCodes, string? companyName = null)
    {
        var isCustomer = string.Equals(entityCode, Models.EntityCodes.Customer, StringComparison.OrdinalIgnoreCase);
        var control = isCustomer ? "300" : "400";
        var letter = LetterFromCompanyName(companyName);
        var existing = existingCodes
            .Select(c => (c ?? "").Trim())
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rx = SeriesPattern(control);
        var bestNum = 0;
        var digitWidth = 4;

        foreach (var code in existing)
        {
            var m = rx.Match(code);
            if (!m.Success)
                continue;
            var seriesLetter = char.ToUpperInvariant(m.Groups[1].Value[0]);
            if (seriesLetter != letter)
                continue;
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num))
                continue;
            if (num > bestNum)
            {
                bestNum = num;
                digitWidth = Math.Clamp(m.Groups[2].Value.Length, 4, 6);
            }
        }

        var next = bestNum + 1;
        for (var i = 0; i < 10_000; i++, next++)
        {
            var digits = next.ToString($"D{digitWidth}", CultureInfo.InvariantCulture);
            var candidate = $"{control}-{letter}{digits}";
            if (candidate.Length > MaxLength)
            {
                digitWidth = Math.Max(1, MaxLength - control.Length - 2);
                digits = next.ToString($"D{digitWidth}", CultureInfo.InvariantCulture);
                candidate = $"{control}-{letter}{digits}";
                if (candidate.Length > MaxLength)
                    break;
            }
            if (!existing.Contains(candidate))
                return candidate;
        }

        return $"{control}-{letter}0001";
    }

    private static Regex SeriesPattern(string control) => control switch
    {
        "300" => CustomerSeriesPattern(),
        _ => SupplierSeriesPattern()
    };

    [GeneratedRegex(@"^[A-Za-z0-9\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^300-([A-Za-z0-9])(\d{1,6})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CustomerSeriesPattern();

    [GeneratedRegex(@"^400-([A-Za-z0-9])(\d{1,6})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SupplierSeriesPattern();
}
