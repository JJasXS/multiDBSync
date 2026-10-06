using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Documents;

/// <summary>Shared date, stamp, and line math for SQL Accounting document posts.</summary>
public static class DocumentPayload
{
    public static string DocDate(DocumentHeader document) =>
        FormatDate(document.DocDate ?? document.PostDate ?? DateTime.Today);

    public static string PostDate(DocumentHeader document) =>
        FormatDate(document.PostDate ?? document.DocDate ?? DateTime.Today);

    public static string TaxDate(DocumentHeader document) =>
        FormatDate(document.TaxDate ?? document.DocDate ?? DateTime.Today);

    public static string PartyCode(DocumentHeader document) => (document.PartyCode ?? "").Trim();

    public static string Currency(DocumentHeader document) => NullIfEmpty(document.Currency) ?? "----";

    public static string Rate(DocumentHeader document) => NullIfEmpty(document.CurrencyRate) ?? "1";

    public static string DocAmount(DocumentHeader document) => Dec(document.Amount);

    public static string StampNote(string? note, string txnStamp)
    {
        var clean = string.IsNullOrWhiteSpace(note) ? "" : note.Trim();
        if (string.IsNullOrWhiteSpace(txnStamp))
            return clean;
        return clean.Length == 0 ? $"SYNC|{txnStamp}" : $"{clean} | SYNC|{txnStamp}";
    }

    public static LineAmounts Line(DocumentLine line, int index)
    {
        var seq = line.Seq > 0 ? line.Seq : (index + 1) * 1000;
        var qty = Dec(line.Qty);
        var unit = Dec(line.UnitPrice);
        var amount = line.Amount != 0 ? Dec(line.Amount) : Dec(line.Qty * line.UnitPrice);
        return new LineAmounts(seq, qty, unit, amount);
    }

    public static string Project(DocumentLine line, DocumentHeader document) =>
        NullIfEmpty(line.Project) ?? NullIfEmpty(document.Project) ?? "----";

    public static void CopyUdf(Dictionary<string, object?> row, DocumentLine line)
    {
        foreach (var (key, value) in line.Fields)
        {
            if (key.StartsWith("udf_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                row[key.ToLowerInvariant()] = value.Trim();
        }
    }

    public static void CopyIfPresent(Dictionary<string, object?> payload, Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                payload[key] = value.Trim();
        }
    }

    public static string FieldOr(Dictionary<string, string> fields, string key, string fallback) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;

    public static string FormatDate(DateTime dt) => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Dec(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string Serialize(Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
}

public readonly record struct LineAmounts(int Seq, string Qty, string Unit, string Amount);
