using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Documents;

/// <summary>Builds SQL Accounting POST /goodsreceived body from a translated document.</summary>
public static class GoodsReceivedMapper
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
        var note = DocumentPayload.StampNote(document.Note, txnStamp);

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
                ["receiveqty"] = qty,
                ["returnqty"] = "0",
                ["qty"] = qty,
                ["uom"] = DocumentPayload.NullIfEmpty(l.Uom) ?? "UNIT",
                ["rate"] = DocumentPayload.NullIfEmpty(l.Rate) ?? "1",
                ["sqty"] = qty,
                ["suomqty"] = qty,
                ["unitprice"] = unit,
                ["disc"] = DocumentPayload.NullIfEmpty(l.Disc),
                ["tax"] = DocumentPayload.NullIfEmpty(l.TaxCode),
                ["irbm_classification"] = DocumentPayload.NullIfEmpty(l.IrbmClassification),
                ["taxamt"] = "0",
                ["localtaxamt"] = "0",
                ["exempted_taxamt"] = "0",
                ["taxinclusive"] = l.TaxInclusive,
                ["amount"] = amt,
                ["localamount"] = amt,
                ["landingcost1"] = "0",
                ["landingcost2"] = "0",
                ["amountwithtax"] = amt,
                ["printable"] = true,
                ["transferable"] = true,
                ["changed"] = true,
                ["sdsserialnumber"] = Array.Empty<object>()
            };

            DocumentPayload.CopyUdf(row, l);
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
            ["description"] = DocumentPayload.NullIfEmpty(document.Description) ?? "Goods received",
            ["cancelled"] = false,
            ["status"] = 0,
            ["docamt"] = docAmt,
            ["localdocamt"] = docAmt,
            ["landingcost1"] = "0",
            ["landingcost2"] = "0",
            ["localtotalwithcost"] = docAmt,
            ["d_amount"] = "0",
            ["branchname"] = DocumentPayload.FieldOr(document.Fields, "branchname", "BILLING"),
            ["dcountry"] = DocumentPayload.FieldOr(document.Fields, "dcountry", "MY"),
            ["sic"] = DocumentPayload.FieldOr(document.Fields, "sic", "00000"),
            ["submissiontype"] = 0,
            ["note"] = DocumentPayload.NullIfEmpty(note),
            ["transferable"] = true,
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
            "docref1", "docref2", "docref3", "docref4");

        if (!string.IsNullOrWhiteSpace(txnStamp))
            payload["udf_txnid"] = txnStamp;

        return payload;
    }

    public static string SerializePayload(Dictionary<string, object?> payload) =>
        DocumentPayload.Serialize(payload);
}
