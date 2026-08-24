using System.Globalization;
using System.Text.Json;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Documents;

/// <summary>Builds SQL Accounting POST /purchaseinvoice body from a translated document.</summary>
public static class PurchaseInvoiceMapper
{
    public static Dictionary<string, object?> ToApiPayload(DocumentHeader document, string txnStamp)
    {
        var docDate = FormatDate(document.DocDate ?? document.PostDate ?? DateTime.Today);
        var postDate = FormatDate(document.PostDate ?? document.DocDate ?? DateTime.Today);
        var taxDate = FormatDate(document.TaxDate ?? document.DocDate ?? DateTime.Today);
        var code = (document.PartyCode ?? "").Trim();
        var currency = NullIfEmpty(document.Currency) ?? "----";
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
            var delivery = FormatDate(l.DeliveryDate ?? document.DocDate ?? DateTime.Today);
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["seq"] = seq,
                ["itemcode"] = NullIfEmpty(l.ItemCode),
                ["location"] = NullIfEmpty(l.Warehouse) ?? "----",
                ["batch"] = NullIfEmpty(l.Batch),
                ["project"] = NullIfEmpty(l.Project) ?? NullIfEmpty(document.Project) ?? "----",
                ["description"] = NullIfEmpty(l.Description),
                ["description2"] = NullIfEmpty(l.Description2),
                ["qty"] = qty,
                ["uom"] = NullIfEmpty(l.Uom) ?? "PCS",
                ["rate"] = NullIfEmpty(l.Rate) ?? "1",
                ["sqty"] = qty,
                ["suomqty"] = "0",
                ["unitprice"] = unit,
                ["deliverydate"] = delivery,
                ["disc"] = NullIfEmpty(l.Disc),
                ["tax"] = NullIfEmpty(l.TaxCode),
                ["irbm_classification"] = NullIfEmpty(l.IrbmClassification) ?? "036",
                ["taxamt"] = "0",
                ["localtaxamt"] = "0",
                ["exempted_taxamt"] = "0",
                ["taxinclusive"] = l.TaxInclusive,
                ["amount"] = amt,
                ["localamount"] = amt,
                ["taxableamt"] = amt,
                ["landingcost1"] = "0",
                ["landingcost2"] = "0",
                ["account"] = NullIfEmpty(l.Account) ?? "610-000",
                ["whlocaltaxamt"] = "0",
                ["printable"] = true,
                ["transferable"] = true,
                ["changed"] = true
            };

            foreach (var (k, v) in l.Fields)
            {
                if (k.StartsWith("udf_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v))
                    row[k.ToLowerInvariant()] = v.Trim();
            }

            return row;
        }).ToList();

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["docdate"] = docDate,
            ["postdate"] = postDate,
            ["taxdate"] = taxDate,
            ["code"] = code,
            ["companyname"] = NullIfEmpty(document.PartyName),
            ["country"] = FieldOr(document.Fields, "country", "MY"),
            ["area"] = NullIfEmpty(document.Area) ?? "----",
            ["agent"] = NullIfEmpty(document.Agent) ?? "----",
            ["project"] = NullIfEmpty(document.Project) ?? "----",
            ["terms"] = NullIfEmpty(document.Terms) ?? "30 Days",
            ["currencycode"] = currency,
            ["currencyrate"] = rate,
            ["shipper"] = FieldOr(document.Fields, "shipper", "----"),
            ["description"] = NullIfEmpty(document.Description) ?? "Purchase",
            ["cancelled"] = false,
            ["status"] = 0,
            ["docamt"] = docAmt,
            ["localdocamt"] = docAmt,
            ["landingcost1"] = "0",
            ["landingcost2"] = "0",
            ["localtotalwithcost"] = docAmt,
            ["d_amount"] = "0",
            ["branchname"] = FieldOr(document.Fields, "branchname", "BILLING"),
            ["dcountry"] = FieldOr(document.Fields, "dcountry", "MY"),
            ["sic"] = FieldOr(document.Fields, "sic", "00000"),
            ["submissiontype"] = 0,
            ["irbm_status"] = 0,
            ["note"] = NullIfEmpty(note),
            ["transferable"] = true,
            ["printcount"] = 0,
            ["sdsdocdetail"] = sdsdocdetail,
            ["changed"] = true
        };

        // Optional address / tax IDs from party fields when present.
        CopyIfPresent(payload, document.Fields,
            "address1", "address2", "address3", "address4", "postcode", "city", "state",
            "phone1", "mobile", "fax1", "attention",
            "daddress1", "daddress2", "daddress3", "daddress4", "dpostcode", "dcity", "dstate",
            "dattention", "dphone1", "dmobile", "dfax1",
            "taxexemptno", "salestaxno", "servicetaxno", "tin", "idno", "businessunit",
            "docref1", "docref2", "docref3", "docref4");

        if (!string.IsNullOrWhiteSpace(txnStamp))
            payload["udf_txnid"] = txnStamp;

        // Leave docno empty so SQL Accounting auto-numbers the PI.
        return payload;
    }

    public static string SerializePayload(Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

    private static void CopyIfPresent(Dictionary<string, object?> payload, Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                payload[key] = v.Trim();
        }
    }

    private static string FieldOr(Dictionary<string, string> fields, string key, string fallback) =>
        fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    private static string FormatDate(DateTime dt) => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Dec(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
