using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Documents;

/// <summary>Builds SQL Accounting POST /stocktransfer body from a translated document.</summary>
public static class StockTransferMapper
{
    public static Dictionary<string, object?> ToApiPayload(DocumentHeader document, string txnStamp)
    {
        var docDate = DocumentPayload.DocDate(document);
        var postDate = DocumentPayload.PostDate(document);
        var code = DocumentPayload.PartyCode(document);
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
            var fromLoc = DocumentPayload.NullIfEmpty(l.FromLocation) ?? DocumentPayload.NullIfEmpty(l.Warehouse) ?? "----";
            var toLoc = DocumentPayload.NullIfEmpty(l.ToLocation) ?? "----";
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["seq"] = seq,
                ["itemcode"] = DocumentPayload.NullIfEmpty(l.ItemCode),
                ["fromlocation"] = fromLoc,
                ["tolocation"] = toLoc,
                ["batch"] = DocumentPayload.NullIfEmpty(l.Batch),
                ["project"] = DocumentPayload.Project(l, document),
                ["description"] = DocumentPayload.NullIfEmpty(l.Description),
                ["description2"] = DocumentPayload.NullIfEmpty(l.Description2),
                ["uom"] = DocumentPayload.NullIfEmpty(l.Uom) ?? "PCS",
                ["qty"] = qty,
                ["sqty"] = qty,
                ["suomqty"] = "0",
                ["rate"] = DocumentPayload.NullIfEmpty(l.Rate) ?? "1",
                ["unitprice"] = unit,
                ["disc"] = DocumentPayload.NullIfEmpty(l.Disc),
                ["tax"] = DocumentPayload.NullIfEmpty(l.TaxCode),
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
            ["code"] = DocumentPayload.NullIfEmpty(code),
            ["companyname"] = DocumentPayload.NullIfEmpty(document.PartyName),
            ["currencyrate"] = rate,
            ["description"] = DocumentPayload.NullIfEmpty(document.Description) ?? "Stock transfer",
            ["area"] = DocumentPayload.NullIfEmpty(document.Area) ?? "----",
            ["agent"] = DocumentPayload.NullIfEmpty(document.Agent) ?? "----",
            ["docamt"] = docAmt,
            ["localdocamt"] = docAmt,
            ["cancelled"] = false,
            ["status"] = 0,
            ["reason"] = DocumentPayload.FieldOr(document.Fields, "reason", ""),
            ["remark"] = DocumentPayload.FieldOr(document.Fields, "remark", ""),
            ["note"] = DocumentPayload.NullIfEmpty(note),
            ["printcount"] = 0,
            ["sdsdocdetail"] = sdsdocdetail,
            ["changed"] = true
        };
    }

    public static string SerializePayload(Dictionary<string, object?> payload) =>
        DocumentPayload.Serialize(payload);
}
