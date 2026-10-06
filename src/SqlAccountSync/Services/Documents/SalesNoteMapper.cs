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
        DocumentPayload.Serialize(payload);

    private static Dictionary<string, object?> BuildPayload(DocumentHeader document, string txnStamp, bool isDebitNote)
    {
        var docDate = DocumentPayload.DocDate(document);
        var postDate = DocumentPayload.PostDate(document);
        var taxDate = DocumentPayload.TaxDate(document);
        var code = DocumentPayload.PartyCode(document);
        var currency = DocumentPayload.Currency(document);
        var rate = DocumentPayload.Rate(document);
        var docAmt = DocumentPayload.DocAmount(document);
        var note = DocumentPayload.StampNote(document.Note, txnStamp);

        var defaultDesc = isDebitNote ? "Sales debit note" : "Sales credit note";
        var lines = document.Lines.Count > 0 ? document.Lines : [];
        var sdsdocdetail = lines.Select((l, i) => BuildLine(l, i, document, isDebitNote)).ToList();

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
            ["description"] = DocumentPayload.NullIfEmpty(document.Description) ?? defaultDesc,
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

        if (isDebitNote)
            payload["transferable"] = true;

        DocumentPayload.CopyIfPresent(payload, document.Fields,
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
        var amounts = DocumentPayload.Line(line, index);
        var seq = amounts.Seq;
        var qty = amounts.Qty;
        var unit = amounts.Unit;
        var amt = amounts.Amount;
        var delivery = DocumentPayload.FormatDate(line.DeliveryDate ?? document.DocDate ?? DateTime.Today);

        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["seq"] = seq,
            ["itemcode"] = DocumentPayload.NullIfEmpty(line.ItemCode),
            ["location"] = DocumentPayload.NullIfEmpty(line.Warehouse) ?? "----",
            ["batch"] = DocumentPayload.NullIfEmpty(line.Batch),
            ["project"] = DocumentPayload.Project(line, document),
            ["description"] = DocumentPayload.NullIfEmpty(line.Description),
            ["description2"] = DocumentPayload.NullIfEmpty(line.Description2),
            ["qty"] = qty,
            ["uom"] = DocumentPayload.NullIfEmpty(line.Uom) ?? "PCS",
            ["rate"] = DocumentPayload.NullIfEmpty(line.Rate) ?? "1",
            ["sqty"] = qty,
            ["suomqty"] = "0",
            ["unitprice"] = unit,
            ["disc"] = DocumentPayload.NullIfEmpty(line.Disc),
            ["tax"] = DocumentPayload.NullIfEmpty(line.TaxCode),
            ["irbm_classification"] = DocumentPayload.NullIfEmpty(line.IrbmClassification) ?? "036",
            ["taxamt"] = "0",
            ["localtaxamt"] = "0",
            ["exempted_taxamt"] = "0",
            ["taxinclusive"] = line.TaxInclusive,
            ["amount"] = amt,
            ["localamount"] = amt,
            ["amountwithtax"] = amt,
            ["account"] = DocumentPayload.NullIfEmpty(line.Account),
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

}
