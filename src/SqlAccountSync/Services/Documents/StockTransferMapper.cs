using System.Globalization;
using System.Text.Json;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Documents;

/// <summary>Builds SQL Accounting POST /stocktransfer body from a translated document.</summary>
public static class StockTransferMapper
{
    public static Dictionary<string, object?> ToApiPayload(DocumentHeader document, string txnStamp)
    {
        var docDate = FormatDate(document.DocDate ?? document.PostDate ?? DateTime.Today);
        var postDate = FormatDate(document.PostDate ?? document.DocDate ?? DateTime.Today);
        var code = (document.PartyCode ?? "").Trim();
        var rate = NullIfEmpty(document.CurrencyRate) ?? "1";
        var docAmt = Dec(document.Amount);

        var note = string.IsNullOrWhiteSpace(document.Note) ? "" : document.Note.Trim();
        if (!string.IsNullOrWhiteSpace(txnStamp))
            note = string.IsNullOrWhiteSpace(note) ? $"SYNC|{txnStamp}" : $"{note} | SYNC|{txnStamp}";

        var lines = document.Lines.Count > 0 ? document.Lines : [];
        var sdsdocdetail = lines.Select((l, i) =>
        {
            var seq = l.Seq > 0 ? l.Seq : (i + 1) * 1000;
            var qty = Dec(l.Qty);
            var unit = Dec(l.UnitPrice);
            var amt = l.Amount != 0 ? Dec(l.Amount) : Dec(l.Qty * l.UnitPrice);
            var fromLoc = NullIfEmpty(l.FromLocation) ?? NullIfEmpty(l.Warehouse) ?? "----";
            var toLoc = NullIfEmpty(l.ToLocation) ?? "----";
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["seq"] = seq,
                ["itemcode"] = NullIfEmpty(l.ItemCode),
                ["fromlocation"] = fromLoc,
                ["tolocation"] = toLoc,
                ["batch"] = NullIfEmpty(l.Batch),
                ["project"] = NullIfEmpty(l.Project) ?? NullIfEmpty(document.Project) ?? "----",
                ["description"] = NullIfEmpty(l.Description),
                ["description2"] = NullIfEmpty(l.Description2),
                ["uom"] = NullIfEmpty(l.Uom) ?? "PCS",
                ["qty"] = qty,
                ["sqty"] = qty,
                ["suomqty"] = "0",
                ["rate"] = NullIfEmpty(l.Rate) ?? "1",
                ["unitprice"] = unit,
                ["disc"] = NullIfEmpty(l.Disc),
                ["tax"] = NullIfEmpty(l.TaxCode),
                ["taxamt"] = "0",
                ["localtaxamt"] = "0",
                ["taxinclusive"] = l.TaxInclusive,
                ["amount"] = amt,
                ["localamount"] = amt,
                ["unitcost"] = unit,
                ["printable"] = true,
                ["changed"] = true
            };
        }).ToList();

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["docdate"] = docDate,
            ["postdate"] = postDate,
            ["code"] = NullIfEmpty(code),
            ["companyname"] = NullIfEmpty(document.PartyName),
            ["currencyrate"] = rate,
            ["description"] = NullIfEmpty(document.Description) ?? "Stock transfer",
            ["area"] = NullIfEmpty(document.Area) ?? "----",
            ["agent"] = NullIfEmpty(document.Agent) ?? "----",
            ["docamt"] = docAmt,
            ["localdocamt"] = docAmt,
            ["cancelled"] = false,
            ["status"] = 0,
            ["reason"] = FieldOr(document.Fields, "reason", ""),
            ["remark"] = FieldOr(document.Fields, "remark", ""),
            ["note"] = NullIfEmpty(note),
            ["printcount"] = 0,
            ["sdsdocdetail"] = sdsdocdetail,
            ["changed"] = true
        };
    }

    public static string SerializePayload(Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

    private static string FieldOr(Dictionary<string, string> fields, string key, string fallback) =>
        fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    private static string FormatDate(DateTime dt) => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Dec(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
