using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Documents;

/// <summary>Builds SQL Accounting POST /purchasereturned body from a translated document.</summary>
public static class PurchaseReturnMapper
{
    public static Dictionary<string, object?> ToApiPayload(DocumentHeader document, string txnStamp)
    {
        var docDate = DocumentPayload.DocDate(document);
        var postDate = DocumentPayload.PostDate(document);
        var taxDate = DocumentPayload.TaxDate(document);
        var code = DocumentPayload.PartyCode(document);
        var currency = DocumentPayload.Currency(document);
        var rate = DocumentPayload.Rate(document);
        var docAmt = DocumentPayload.DocAmount(document);
        var note = DocumentPayload.StampNote(SanitizeNote(document.Note), txnStamp);

        var lines = document.Lines.Count > 0 ? document.Lines : [];
        var sdsdocdetail = lines.Select((l, i) =>
        {
            var amounts = DocumentPayload.Line(l, i);
            var seq = amounts.Seq;
            var qty = amounts.Qty;
            var unit = amounts.Unit;
            var amt = amounts.Amount;
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["seq"] = seq,
                ["itemcode"] = DocumentPayload.NullIfEmpty(l.ItemCode),
                ["location"] = DocumentPayload.NullIfEmpty(l.Warehouse) ?? "----",
                ["batch"] = DocumentPayload.NullIfEmpty(l.Batch),
                ["project"] = DocumentPayload.Project(l, document),
                ["description"] = DocumentPayload.NullIfEmpty(l.Description),
                ["description2"] = DocumentPayload.NullIfEmpty(l.Description2),
                ["qty"] = qty,
                ["uom"] = DocumentPayload.NullIfEmpty(l.Uom) ?? "PCS",
                ["rate"] = DocumentPayload.NullIfEmpty(l.Rate) ?? "1",
                ["sqty"] = qty,
                ["suomqty"] = "0",
                ["unitprice"] = unit,
                ["disc"] = DocumentPayload.NullIfEmpty(l.Disc),
                ["tax"] = DocumentPayload.NullIfEmpty(l.TaxCode),
                ["irbm_classification"] = DocumentPayload.NullIfEmpty(l.IrbmClassification) ?? "036",
                ["taxamt"] = "0",
                ["localtaxamt"] = "0",
                ["exempted_taxamt"] = "0",
                ["taxinclusive"] = l.TaxInclusive,
                ["amount"] = amt,
                ["localamount"] = amt,
                ["amountwithtax"] = amt,
                ["account"] = DocumentPayload.NullIfEmpty(l.Account) ?? "610-000",
                ["whlocaltaxamt"] = "0",
                ["printable"] = true,
                ["changed"] = true
            };

            return row;
        }).ToList();

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["docdate"] = docDate,
            ["postdate"] = postDate,
            ["taxdate"] = taxDate,
            ["code"] = code,
            ["companyname"] = DocumentPayload.NullIfEmpty(document.PartyName),
            ["country"] = DocumentPayload.FieldOr(document.Fields, "country", "MY"),
            ["area"] = DocumentPayload.NullIfEmpty(document.Area) ?? "----",
            ["agent"] = DocumentPayload.NullIfEmpty(document.Agent) ?? "----",
            ["project"] = DocumentPayload.NullIfEmpty(document.Project) ?? "----",
            ["terms"] = DocumentPayload.NullIfEmpty(document.Terms) ?? "30 Days",
            ["currencycode"] = currency,
            ["currencyrate"] = rate,
            ["shipper"] = DocumentPayload.FieldOr(document.Fields, "shipper", "----"),
            ["description"] = DocumentPayload.NullIfEmpty(document.Description) ?? "Purchase return",
            ["cancelled"] = false,
            ["status"] = 0,
            ["docamt"] = docAmt,
            ["localdocamt"] = docAmt,
            ["branchname"] = DocumentPayload.FieldOr(document.Fields, "branchname", "BILLING"),
            ["dcountry"] = DocumentPayload.FieldOr(document.Fields, "dcountry", "MY"),
            ["sic"] = DocumentPayload.FieldOr(document.Fields, "sic", "00000"),
            ["submissiontype"] = 0,
            ["irbm_status"] = 0,
            ["note"] = DocumentPayload.NullIfEmpty(note),
            ["printcount"] = 0,
            ["sdsdocdetail"] = sdsdocdetail,
            ["changed"] = true
        };

        DocumentPayload.CopyIfPresent(payload, document.Fields,
            "address1", "address2", "address3", "address4", "postcode", "city", "state",
            "phone1", "mobile", "fax1", "attention",
            "daddress1", "daddress2", "daddress3", "daddress4", "dpostcode", "dcity", "dstate",
            "dattention", "dphone1", "dmobile", "dfax1",
            "taxexemptno", "salestaxno", "servicetaxno", "tin", "idno", "businessunit",
            "docref1", "docref2", "docref3", "docref4", "fromdoc");

        return payload;
    }

    public static string SerializePayload(Dictionary<string, object?> payload) =>
        DocumentPayload.Serialize(payload);

    private static string SanitizeNote(string? note)
    {
        var clean = (note ?? "").Trim();
        if (clean.Length == 0)
            return "";
        // Firebird NOTE can hold encrypted blobs; never POST binary/control garbage.
        if (clean.Length > 500 || clean.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            return "";
        return clean;
    }
}
