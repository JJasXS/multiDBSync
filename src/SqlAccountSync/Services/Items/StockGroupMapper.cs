using System.Text.Json;

namespace SqlAccountSync.Services.Items;

public sealed class StockGroupRecord
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string Sales { get; set; } = "";
    public string SalesReturned { get; set; } = "";
    public string CashSales { get; set; } = "";
    public string Purchase { get; set; } = "";
    public string CashPurchase { get; set; } = "";
    public string PurchaseReturned { get; set; } = "";
    public string BalanceStock { get; set; } = "";
    public int CostingMethod { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

public static class StockGroupMapper
{
    public static Dictionary<string, object?> ToApiPayload(StockGroupRecord group) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = group.Code,
        ["description"] = string.IsNullOrWhiteSpace(group.Description) ? group.Code : group.Description,
        ["sales"] = group.Sales,
        ["salesreturned"] = group.SalesReturned,
        ["cashsales"] = group.CashSales,
        ["purchase"] = group.Purchase,
        ["cashpurchase"] = group.CashPurchase,
        ["purchasereturned"] = group.PurchaseReturned,
        ["balancestock"] = group.BalanceStock,
        ["costingmethod"] = group.CostingMethod,
        ["isactive"] = group.IsActive,
        ["lastmodified"] = 0
    };

    public static string SerializePayload(Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

    public static StockGroupRecord? TryParseFromApiRow(JsonElement row)
    {
        string? S(string name) => row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()?.Trim()
            : null;
        var code = S("code");
        if (string.IsNullOrWhiteSpace(code))
            return null;
        return new StockGroupRecord
        {
            Code = code,
            Description = S("description") ?? code,
            Sales = S("sales") ?? "",
            SalesReturned = S("salesreturned") ?? "",
            CashSales = S("cashsales") ?? "",
            Purchase = S("purchase") ?? "",
            CashPurchase = S("cashpurchase") ?? "",
            PurchaseReturned = S("purchasereturned") ?? "",
            BalanceStock = S("balancestock") ?? "",
            CostingMethod = row.TryGetProperty("costingmethod", out var cm) && cm.TryGetInt32(out var n) ? n : 1,
            IsActive = !row.TryGetProperty("isactive", out var ia) || ia.ValueKind != JsonValueKind.False
        };
    }
}
