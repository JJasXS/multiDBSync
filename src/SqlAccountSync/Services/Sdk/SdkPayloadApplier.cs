using System.Globalization;

namespace SqlAccountSync.Services.Sdk;

/// <summary>Applies mapper JSON dictionaries onto an open SQL Account dataset.</summary>
public static class SdkPayloadApplier
{
    public static int ApplyMain(dynamic dataSet, IDictionary<string, object?> payload)
    {
        var applied = 0;
        foreach (var (key, value) in payload)
        {
            if (SdkObjectMap.SkipPayloadKeys.Contains(key))
                continue;
            if (value is IEnumerable<object> or System.Collections.IList && value is not string)
                continue;
            if (TrySetField(dataSet, SdkObjectMap.ToDatasetField(key), value))
                applied++;
        }
        return applied;
    }

    public static int ApplyDetailRows(dynamic detailDataSet, IEnumerable<object?> rows)
    {
        var count = 0;
        foreach (var rowObj in rows)
        {
            if (rowObj is not IDictionary<string, object?> row
                && rowObj is not Dictionary<string, object?>)
                continue;

            var dict = rowObj as IDictionary<string, object?>
                ?? ((Dictionary<string, object?>)rowObj)
                    .ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);

            try { detailDataSet.Append(); }
            catch
            {
                try { detailDataSet.Insert(); }
                catch { continue; }
            }

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
                    catch { field.Value = b ? 1 : 0; }
                    break;
                case int i:
                    try { field.AsInteger = i; }
                    catch { field.Value = i; }
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
