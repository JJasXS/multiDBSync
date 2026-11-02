using System.Globalization;

namespace SqlAccountSync.Services.Sdk;

/// <summary>Applies mapper JSON dictionaries onto an open SQL Account dataset.</summary>
public static class SdkPayloadApplier
{
    public static int ApplyMain(dynamic dataSet, IDictionary<string, object?> payload)
    {
        // Do not force Edit/Insert here — BizObject.New() already leaves MainDataSet in insert mode.
        // Calling Edit/Insert again on some company DBs leaves integer fields as Null and Save crashes.
        var applied = 0;
        foreach (var (key, value) in payload)
        {
            if (SdkObjectMap.SkipPayloadKeys.Contains(key))
                continue;
            if (value is IEnumerable<object> or System.Collections.IList && value is not string)
                continue;
            // Skip empty optionals — SQL Acc Save rejects unknown agent/area/category codes.
            if (IsEmptyOptional(key, value))
                continue;
            // Never write dockey=0 on create (API leftover); SDK assigns keys.
            if (key.Equals("dockey", StringComparison.OrdinalIgnoreCase)
                && value is int di && di == 0)
                continue;
            if (key.Equals("dockey", StringComparison.OrdinalIgnoreCase)
                && value is long dl && dl == 0)
                continue;
            if (TrySetField(dataSet, SdkObjectMap.ToDatasetField(key), value))
                applied++;
        }
        return applied;
    }

    private static bool IsEmptyOptional(string key, object? value)
    {
        if (value is null) return true;
        if (value is not string s) return false;
        if (s.Length == 0) return true;
        // Placeholder sent by HTTP API mappers; leave SQL Acc defaults instead.
        if (s is "----" or "-" or "n/a")
        {
            var k = key.ToLowerInvariant();
            return k is "agent" or "area" or "companycategory" or "currencycode"
                or "pricetag" or "tax" or "biznature";
        }
        return false;
    }

    private static void EnsureEditable(dynamic dataSet)
    {
        try
        {
            // 0=browse in typical Delphi TDataSet.State; Edit/Insert already ok.
            int state = (int)dataSet.State;
            if (state == 0)
            {
                try { dataSet.Edit(); }
                catch { dataSet.Insert(); }
            }
        }
        catch
        {
            try { dataSet.Edit(); }
            catch
            {
                try { dataSet.Insert(); }
                catch { /* New() usually leaves dataset editable */ }
            }
        }
    }

    public static int ApplyDetailRows(dynamic detailDataSet, IEnumerable<object?> rows)
    {
        // Official SDK samples: New() creates the first detail row — Edit it; Append only for extras.
        var count = 0;
        foreach (var rowObj in rows)
        {
            if (rowObj is not IDictionary<string, object?> row
                && rowObj is not Dictionary<string, object?>)
                continue;

            var dict = rowObj as IDictionary<string, object?>
                ?? ((Dictionary<string, object?>)rowObj)
                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);

            if (!BeginDetailRow(detailDataSet, isFirst: count == 0))
                continue;

            foreach (var (key, value) in dict)
            {
                if (SdkObjectMap.SkipPayloadKeys.Contains(key))
                    continue;
                TrySetField(detailDataSet, SdkObjectMap.ToDatasetField(key), value);
            }

            try { detailDataSet.Post(); }
            catch { /* some datasets auto-post */ }
            count++;
        }
        return count;
    }

    private static bool BeginDetailRow(dynamic detailDataSet, bool isFirst)
    {
        if (isFirst)
        {
            try
            {
                detailDataSet.Edit();
                return true;
            }
            catch
            {
                // fall through to Append/Insert
            }
        }

        try
        {
            detailDataSet.Append();
            return true;
        }
        catch
        {
            try
            {
                detailDataSet.Insert();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool TrySetField(dynamic dataSet, string fieldName, object? value)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return false;
        try
        {
            var field = dataSet.FindField(fieldName);
            if (field is null)
                return false;

            if (value is null)
            {
                try { field.Clear(); }
                catch { field.Value = ""; }
                return true;
            }

            switch (value)
            {
                case bool b:
                    try { field.AsBoolean = b; }
                    catch
                    {
                        try { field.AsString = b ? "T" : "F"; }
                        catch { field.Value = b ? 1 : 0; }
                    }
                    break;
                case int i:
                    // Prefer float for BCD/currency columns (CREDITLIMIT etc.); AsInteger can poison Null.
                    try { field.AsFloat = i; }
                    catch
                    {
                        try { field.AsInteger = i; }
                        catch { field.Value = i; }
                    }
                    break;
                case long l:
                    field.Value = l;
                    break;
                case decimal d:
                    try { field.AsFloat = (double)d; }
                    catch { field.Value = d; }
                    break;
                case double dbl:
                    try { field.AsFloat = dbl; }
                    catch { field.Value = dbl; }
                    break;
                case DateTime dt:
                    try { field.AsDateTime = dt; }
                    catch { field.Value = dt; }
                    break;
                default:
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    if (bool.TryParse(text, out var pb))
                    {
                        try { field.AsBoolean = pb; }
                        catch { field.AsString = text; }
                    }
                    else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pi)
                             && text.IndexOf('.') < 0)
                    {
                        try { field.AsInteger = pi; }
                        catch { field.AsString = text; }
                    }
                    else if ((text.Contains('.') || text.Contains(','))
                             && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var pd))
                    {
                        try { field.AsFloat = (double)pd; }
                        catch { field.AsString = text; }
                    }
                    else if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var pdt)
                             && text.Contains('-'))
                    {
                        try { field.AsDateTime = pdt; }
                        catch { field.AsString = text; }
                    }
                    else
                    {
                        field.AsString = text;
                    }
                    break;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static object? FindNestedList(IDictionary<string, object?> payload, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var pair in payload)
            {
                if (!pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;
                return pair.Value;
            }
        }
        return null;
    }
}
