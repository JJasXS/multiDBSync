using System.Globalization;
using System.Text.Json;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Documents;

/// <summary>
/// Builds SQL Accounting POST /salesdebitnote and /salescreditnote bodies
/// from a translated document. Omits udf_* and Postman placeholder junk.
/// </summary>
public static class SalesNoteMapper
{
    public static Dictionary<string, object?> ToDebitNotePayload(DocumentHeader document, string txnStamp) =>
        BuildPayload(document, txnStamp, isDebitNote: true);

    public static Dictionary<string, object?> ToCreditNotePayload(DocumentHeader document, string txnStamp) =>
        BuildPayload(document, txnStamp, isDebitNote: false);

    public static string SerializePayload(Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

    private static Dictionary<string, object?> BuildPayload(DocumentHeader document, string txnStamp, bool isDebitNote)
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

        var defaultDesc = isDebitNote ? "Sales debit note" : "Sales credit note";
        var lines = document.Lines.Count > 0 ? document.Lines : [];
        var sdsdocdetail = lines.Select((l, i) => BuildLine(l, i, document, isDebitNote)).ToList();

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
            ["description"] = NullIfEmpty(document.Description) ?? defaultDesc,
            ["cancelled"] = false,
            ["status"] = 0,
            ["docamt"] = docAmt,
            ["localdocamt"] = docAmt,
            ["branchname"] = FieldOr(document.Fields, "branchname", "BILLING"),
            ["dcountry"] = FieldOr(document.Fields, "dcountry", "MY"),
            ["sic"] = FieldOr(document.Fields, "sic", "00000"),
            ["submissiontype"] = 0,
            ["irbm_status"] = 0,
            ["note"] = NullIfEmpty(note),
            ["printcount"] = 0,
            ["sdsdocdetail"] = sdsdocdetail,
            ["changed"] = true
        };

        if (isDebitNote)
            payload["transferable"] = true;

        CopyIfPresent(payload, document.Fields,
            "address1", "address2", "address3", "address4", "postcode", "city", "state",
            "phone1", "mobile", "fax1", "attention",
            "daddress1", "daddress2", "daddress3", "daddress4", "dpostcode", "dcity", "dstate",
            "dattention", "dphone1", "dmobile", "dfax1",
            "taxexemptno", "salestaxno", "servicetaxno", "tin", "idno", "businessunit",
            "docref1", "docref2", "docref3", "docref4", "fromdoc", "validity", "deliveryterm");

        return payload;
    }

    private static Dictionary<string, object?> BuildLine(
        DocumentLine line, int index, DocumentHeader document, bool isDebitNote)
    {
        var seq = line.Seq > 0 ? line.Seq : (index + 1) * 1000;
        var qty = Dec(line.Qty);
        var unit = Dec(line.UnitPrice);
        var amt = line.Amount != 0 ? Dec(line.Amount) : Dec(line.Qty * line.UnitPrice);
        var delivery = FormatDate(line.DeliveryDate ?? document.DocDate ?? DateTime.Today);

        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["seq"] = seq,
            ["itemcode"] = NullIfEmpty(line.ItemCode),
            ["location"] = NullIfEmpty(line.Warehouse) ?? "----",
            ["batch"] = NullIfEmpty(line.Batch),
            ["project"] = NullIfEmpty(line.Project) ?? NullIfEmpty(document.Project) ?? "----",
            ["description"] = NullIfEmpty(line.Description),
            ["description2"] = NullIfEmpty(line.Description2),
            ["qty"] = qty,
            ["uom"] = NullIfEmpty(line.Uom) ?? "PCS",
            ["rate"] = NullIfEmpty(line.Rate) ?? "1",
            ["sqty"] = qty,
            ["suomqty"] = "0",
            ["unitprice"] = unit,
            ["disc"] = NullIfEmpty(line.Disc),
            ["tax"] = NullIfEmpty(line.TaxCode),
            ["irbm_classification"] = NullIfEmpty(line.IrbmClassification) ?? "036",
            ["taxamt"] = "0",
            ["localtaxamt"] = "0",
            ["exempted_taxamt"] = "0",
            ["taxinclusive"] = line.TaxInclusive,
            ["amount"] = amt,
            ["localamount"] = amt,
            ["amountwithtax"] = amt,
            ["account"] = NullIfEmpty(line.Account),
            ["printable"] = true,
            ["changed"] = true
        };

        if (isDebitNote)
        {
            row["deliverydate"] = delivery;
            row["transferable"] = true;
        }
        else
        {
            row["unitcost"] = unit;
        }

        return row;
    }

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
