using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

public sealed class PnLByProjectCompanyGroup
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Projects { get; init; }
}

public sealed class PnLByProjectMatrixRow
{
    public PnLByProjectMatrixRow(string code, string description, decimal[] amounts)
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

public sealed class PnLByProjectMatrix
{
    public IReadOnlyList<PnLByProjectCompanyGroup> Companies { get; init; } = [];
    public IReadOnlyList<PnLByProjectMatrixRow> Rows { get; init; } = [];

    public int ProjectColumnCount
    {
        get
        {
            var count = 0;
            foreach (var company in Companies)
                count += company.Projects.Count;
            return count;
        }
    }

    public static PnLByProjectMatrix From(IReadOnlyList<DashboardDetailRow> companies)
    {
        var groups = new List<PnLByProjectCompanyGroup>(companies.Count);
        var projectIndexes = new List<Dictionary<string, int>>(companies.Count);
        var flatCount = 0;

        for (var companyIndex = 0; companyIndex < companies.Count; companyIndex++)
        {
            var company = companies[companyIndex];
            var labels = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in company.Records)
            {
                var label = DisplayProject(line.Project);
                if (index.ContainsKey(label))
                    continue;
                index[label] = labels.Count;
                labels.Add(label);
            }

            if (labels.Count == 0)
            {
                // Company selected but no GL lines in range: keep one empty project column.
                labels.Add("");
                index[""] = 0;
            }

            labels.Sort(CompareProjectLabels);
            for (var i = 0; i < labels.Count; i++)
                index[labels[i]] = i;

            groups.Add(new PnLByProjectCompanyGroup
            {
                Name = company.DatabaseName,
                Projects = labels
            });
            projectIndexes.Add(index);
            flatCount += labels.Count;
        }

        var rows = new List<PnLByProjectMatrixRow>();
        var rowIndex = new Dictionary<string, PnLByProjectMatrixRow>(StringComparer.OrdinalIgnoreCase);
        var flatOffset = 0;
        for (var companyIndex = 0; companyIndex < companies.Count; companyIndex++)
        {
            var company = companies[companyIndex];
            var projects = projectIndexes[companyIndex];
            foreach (var line in company.Records)
            {
                var code = line.AccountCode ?? "";
                var key = code.Trim();
                var description = (line.AccountDescription ?? "").Trim();
                if (description.Length == 0)
                    description = (line.Project ?? "").Trim();
                if (!rowIndex.TryGetValue(key, out var row))
                {
                    row = new PnLByProjectMatrixRow(code, description, new decimal[flatCount]);
                    rowIndex[key] = row;
                    rows.Add(row);
                }
                else
                {
                    row.UseDescription(description);
                }

                var label = DisplayProject(line.Project);
                row.Amounts[flatOffset + projects[label]] += line.Amount;
            }

            flatOffset += groups[companyIndex].Projects.Count;
        }

        rows.Sort(static (left, right) =>
        {
            var byCode = string.Compare(left.Code, right.Code, StringComparison.OrdinalIgnoreCase);
            if (byCode != 0)
                return byCode;
            return string.Compare(left.Description, right.Description, StringComparison.OrdinalIgnoreCase);
        });

        return new PnLByProjectMatrix { Companies = groups, Rows = rows };
    }

    /// <summary>
    /// Normalize a GL_TRANS.PROJECT value for column headers.
    /// SQL Account stores the company default project as the literal code "----".
    /// Keep that code visible and do not merge it with NULL/blank values.
    /// </summary>
    public static string DisplayProject(string? project)
    {
        if (project is null)
            return "";
        // Firebird CHAR columns can pad with spaces or trailing NUL bytes.
        var value = project.Replace("\0", "", StringComparison.Ordinal).Trim();
        return value;
    }

    private static int CompareProjectLabels(string left, string right)
    {
        // Keep the SQL Account default project first when present.
        var leftDefault = string.Equals(left, "----", StringComparison.Ordinal);
        var rightDefault = string.Equals(right, "----", StringComparison.Ordinal);
        if (leftDefault != rightDefault)
            return leftDefault ? -1 : 1;
        var leftBlank = left.Length == 0;
        var rightBlank = right.Length == 0;
        if (leftBlank != rightBlank)
            return leftBlank ? 1 : -1;
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
