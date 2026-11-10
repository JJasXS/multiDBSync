using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

public sealed class PlByProjectMatrixRow
{
    public PlByProjectMatrixRow(string code, string description, decimal[] amounts)
    {
        Code = code;
        Description = description;
        Amounts = amounts;
    }

    public string Code { get; }
    public string Description { get; private set; }
    public decimal[] Amounts { get; }

    public decimal Total
    {
        get
        {
            decimal total = 0m;
            foreach (var amount in Amounts)
                total += amount;
            return total;
        }
    }

    public void UseDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(Description) && !string.IsNullOrWhiteSpace(description))
            Description = description.Trim();
    }
}

public sealed class PlByProjectMatrix
{
    public IReadOnlyList<string> Companies { get; init; } = [];
    public IReadOnlyList<PlByProjectMatrixRow> Rows { get; init; } = [];

    public static PlByProjectMatrix From(IReadOnlyList<DashboardDetailRow> companies)
    {
        var names = new List<string>(companies.Count);
        var rows = new List<PlByProjectMatrixRow>();
        var index = new Dictionary<string, PlByProjectMatrixRow>(StringComparer.OrdinalIgnoreCase);
        for (var companyIndex = 0; companyIndex < companies.Count; companyIndex++)
        {
            names.Add(companies[companyIndex].DatabaseName);
            foreach (var line in companies[companyIndex].Records)
            {
                var code = line.AccountCode ?? "";
                var key = code.Trim();
                var description = (line.AccountDescription ?? "").Trim();
                if (description.Length == 0)
                    description = (line.Project ?? "").Trim();
                if (!index.TryGetValue(key, out var row))
                {
                    row = new PlByProjectMatrixRow(code, description, new decimal[companies.Count]);
                    index[key] = row;
                    rows.Add(row);
                }
                else
                {
                    row.UseDescription(description);
                }
                row.Amounts[companyIndex] += line.Amount;
            }
        }

        rows.Sort(static (left, right) =>
        {
            var byCode = string.Compare(left.Code, right.Code, StringComparison.OrdinalIgnoreCase);
            if (byCode != 0)
                return byCode;
            return string.Compare(left.Description, right.Description, StringComparison.OrdinalIgnoreCase);
        });
        return new PlByProjectMatrix { Companies = names, Rows = rows };
    }
}
