using System.Text.Json;
using SqlAccountSync.Models;

namespace SqlAccountSync.Services.Items;

public sealed class StockItemUom
{
    public string Uom { get; set; } = "";
    public string Rate { get; set; } = "1";
    public string RefCost { get; set; } = "0";
    public string RefPrice { get; set; } = "0";
    public string MinCost { get; set; } = "0";
    public string MaxCost { get; set; } = "0";
    public string MinPrice { get; set; } = "0";
    public string MaxPrice { get; set; } = "0";
    public bool IsBase { get; set; } = true;
}

public sealed class StockItemCategory
{
    public string Category { get; set; } = "";
}

public sealed class StockItem
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string Description2 { get; set; } = "";
    public string Description3 { get; set; } = "";
    public string StockGroup { get; set; } = "";
    public bool StockControl { get; set; } = true;
    public int CostingMethod { get; set; } = 1;
    public bool SerialNumber { get; set; }
    public string Shelf { get; set; } = "";
    public string MinQty { get; set; } = "0";
    public string MaxQty { get; set; } = "0";
    public string Suom { get; set; } = "";
    public string ReorderLevel { get; set; } = "0";
    public string ReorderQty { get; set; } = "1";
    public string SlTax { get; set; } = "";
    public string PhTax { get; set; } = "";
    public string Tariff { get; set; } = "";
    public string IrbmClassification { get; set; } = "022";
    public string StockMatrix { get; set; } = "";
    public string DefUomSt { get; set; } = "";
    public string DefUomSl { get; set; } = "";
    public string DefUomPh { get; set; } = "";
    public string ScriptCode { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string Uom { get; set; } = "";
    public string RefCost { get; set; } = "0";
    public string RefPrice { get; set; } = "0";
    public string ItemType { get; set; } = "-";
    public int LeadTime { get; set; }
    public int BomLeadTime { get; set; }
    public string BomAsmCost { get; set; } = "0";
    public string Remark1 { get; set; } = "";
    public string Remark2 { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string CreationDate { get; set; } = "";
    public List<StockItemUom> Uoms { get; set; } = [];
    public List<StockItemCategory> Categories { get; set; } = [];
    public Dictionary<string, string> UdfFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class StockItemMapper
{
    private static readonly string[] NumericUdfKeys =
    [
        "udf_stdprice", "udf_dleadtime", "udf_moq", "udf_bundle",
        "udf_thickness", "udf_width", "udf_length",
        "udf_ds", "udf_dp", "udf_dsys", "udf_dfs", "udf_dfp", "udf_dfsys",
        "udf_wts", "udf_wtp", "udf_wtsys", "udf_pricekg", "udf_costkg"
    ];

    private static readonly string[] StringUdfKeys =
    [
        "udf_2uom", "udf_formula", "udf_mtype", "udf_uom", "udf_stockgroup"
    ];

    public static StockItem FromMasterRecord(MasterRecord record)
    {
        var f = record.Fields;
        var item = new StockItem
        {
            Code = record.Code,
            Description = First(f, record.Description, "description"),
            Description2 = Field(f, "description2"),
            Description3 = Field(f, "description3"),
            StockGroup = Field(f, "stockgroup") is { Length: > 0 } sg ? sg : Field(f, "udf_stockgroup"),
            StockControl = Bool(f, true, "stockcontrol"),
            CostingMethod = Int(f, 1, "costingmethod"),
            SerialNumber = Bool(f, false, "serialnumber"),
            Shelf = Field(f, "shelf"),
            MinQty = FieldOr(f, "0", "minqty"),
            MaxQty = FieldOr(f, "0", "maxqty"),
            Suom = Field(f, "suom"),
            ReorderLevel = FieldOr(f, "0", "reorderlevel"),
            ReorderQty = FieldOr(f, "1", "reorderqty"),
            SlTax = Field(f, "sltax"),
            PhTax = Field(f, "phtax"),
            Tariff = Field(f, "tariff"),
            IrbmClassification = FieldOr(f, "022", "irbm_classification"),
            StockMatrix = Field(f, "stockmatrix"),
            DefUomSt = Field(f, "defuom_st"),
            DefUomSl = Field(f, "defuom_sl"),
            DefUomPh = Field(f, "defuom_ph"),
            ScriptCode = Field(f, "scriptcode"),
            IsActive = Bool(f, true, "isactive"),
            Uom = Field(f, "uom"),
            RefCost = FieldOr(f, "0", "refcost"),
            RefPrice = FieldOr(f, "0", "refprice"),
            ItemType = FieldOr(f, "-", "itemtype"),
            LeadTime = Int(f, 0, "leadtime"),
            BomLeadTime = Int(f, 0, "bom_leadtime"),
            BomAsmCost = FieldOr(f, "0", "bom_asmcost"),
            Remark1 = Field(f, "remark1"),
            Remark2 = Field(f, "remark2"),
            Barcode = Field(f, "barcode"),
            CreationDate = Field(f, "creationdate")
        };

        if (TryParseUoms(f, out var uoms))
            item.Uoms = uoms;
        else if (!string.IsNullOrWhiteSpace(item.Uom))
            item.Uoms = [new StockItemUom { Uom = item.Uom, Rate = "1", RefCost = item.RefCost, RefPrice = item.RefPrice, IsBase = true }];

        if (TryParseCategories(f, out var cats))
            item.Categories = cats;
        else
        {
            var cat = Field(f, "category");
            if (string.IsNullOrWhiteSpace(cat))
                cat = Field(f, "udf_mtype");
            if (!string.IsNullOrWhiteSpace(cat))
                item.Categories = [new StockItemCategory { Category = cat }];
        }

        foreach (var (k, v) in f)
        {
            if (!k.StartsWith("udf_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(v))
                item.UdfFields[k.ToLowerInvariant()] = v.Trim();
        }

        NormalizeUoms(item);
        return item;
    }

    public static Dictionary<string, object?> ToApiPayload(StockItem item, string suggestedCode, string txnStamp)
    {
        NormalizeUoms(item);
        var code = (suggestedCode ?? "").Trim();
        if (code.Length == 0)
            code = item.Code;

        var remark1 = string.IsNullOrWhiteSpace(item.Remark1) ? "" : item.Remark1;
        if (!string.IsNullOrWhiteSpace(txnStamp))
            remark1 = string.IsNullOrWhiteSpace(remark1) ? $"SYNC|{txnStamp}" : $"{remark1} | SYNC|{txnStamp}";

        var uom = item.Uom is { Length: > 0 } ? item.Uom : item.Uoms.FirstOrDefault(u => u.IsBase)?.Uom ?? "PCS";
        var suom = item.Suom is { Length: > 0 } ? item.Suom : uom;
        var uoms = item.Uoms.Count > 0
            ? item.Uoms
            : [new StockItemUom { Uom = uom, Rate = "1", RefCost = item.RefCost, RefPrice = item.RefPrice, IsBase = true }];

        var sdsuom = uoms.Select(u => new Dictionary<string, object?>
        {
            ["code"] = code,
            ["uom"] = NullIfEmpty(u.Uom) ?? uom,
            ["rate"] = NullIfEmpty(u.Rate) ?? "1",
            ["refcost"] = NullIfEmpty(u.RefCost) ?? "0",
            ["refprice"] = NullIfEmpty(u.RefPrice) ?? "0",
            ["mincost"] = NullIfEmpty(u.MinCost) ?? "0",
            ["maxcost"] = NullIfEmpty(u.MaxCost) ?? "0",
            ["minprice"] = NullIfEmpty(u.MinPrice) ?? "0",
            ["maxprice"] = NullIfEmpty(u.MaxPrice) ?? "0",
            ["isbase"] = u.IsBase
        }).ToList();

        var sdscategory = item.Categories
            .Where(c => !string.IsNullOrWhiteSpace(c.Category))
            .Select(c => new Dictionary<string, object?>
            {
                ["code"] = code,
                ["category"] = c.Category.Trim()
            }).ToList();

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["dockey"] = 0,
            ["dirty"] = true,
            ["lastmodified"] = 0,
            ["code"] = code,
            ["description"] = item.Description,
            ["description2"] = NullIfEmpty(item.Description2),
            ["description3"] = NullIfEmpty(item.Description3),
            ["stockgroup"] = NullIfEmpty(item.StockGroup),
            ["stockcontrol"] = item.StockControl,
            ["costingmethod"] = item.CostingMethod,
            ["serialnumber"] = item.SerialNumber,
            ["shelf"] = NullIfEmpty(item.Shelf),
            ["minqty"] = item.MinQty,
            ["maxqty"] = item.MaxQty,
            ["suom"] = suom,
            ["reorderlevel"] = item.ReorderLevel,
            ["reorderqty"] = item.ReorderQty,
            ["sltax"] = NullIfEmpty(item.SlTax),
            ["phtax"] = NullIfEmpty(item.PhTax),
            ["tariff"] = NullIfEmpty(item.Tariff),
            ["irbm_classification"] = NullIfEmpty(item.IrbmClassification) ?? "022",
            ["stockmatrix"] = NullIfEmpty(item.StockMatrix),
            ["defuom_st"] = NullIfEmpty(item.DefUomSt),
            ["defuom_sl"] = NullIfEmpty(item.DefUomSl),
            ["defuom_ph"] = NullIfEmpty(item.DefUomPh),
            ["scriptcode"] = NullIfEmpty(item.ScriptCode),
            ["isactive"] = item.IsActive,
            ["creationdate"] = NormalizeCreationDate(item.CreationDate) ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["uom"] = uom,
            ["refcost"] = item.RefCost,
            ["refprice"] = item.RefPrice,
            ["itemtype"] = NullIfEmpty(item.ItemType) ?? "-",
            ["leadtime"] = item.LeadTime,
            ["bom_leadtime"] = item.BomLeadTime,
            ["bom_asmcost"] = item.BomAsmCost,
            ["balsqty"] = "0",
            ["balsuomqty"] = "0",
            ["remark1"] = NullIfEmpty(remark1),
            ["remark2"] = NullIfEmpty(item.Remark2),
            ["barcode"] = NullIfEmpty(item.Barcode),
            ["sdsuom"] = sdsuom,
            ["sdsopeningbalance"] = Array.Empty<object>(),
            ["sdscustomerprice"] = Array.Empty<object>(),
            ["sdssupplierprice"] = Array.Empty<object>(),
            ["sdsbom"] = Array.Empty<object>(),
            ["sdsaltstockitem"] = Array.Empty<object>(),
            ["sdscustomeritem"] = Array.Empty<object>(),
            ["sdssupplieritem"] = Array.Empty<object>(),
            ["sdsbarcode"] = Array.Empty<object>()
        };

        if (sdscategory.Count > 0)
            payload["sdscategory"] = sdscategory;

        foreach (var (k, v) in item.UdfFields)
            payload[k] = k.Equals("udf_direct", StringComparison.OrdinalIgnoreCase)
                ? ParseUdfDirect(v)
                : v;

        ApplyUdfDefaults(payload, item.UdfFields);

        return payload;
    }

    private static void ApplyUdfDefaults(Dictionary<string, object?> payload, Dictionary<string, string> sourceUdfs)
    {
        foreach (var key in NumericUdfKeys)
        {
            if (payload.ContainsKey(key))
                continue;
            payload[key] = "0";
        }

        foreach (var key in StringUdfKeys)
        {
            if (payload.ContainsKey(key))
                continue;
            payload[key] = "";
        }

        if (!payload.ContainsKey("udf_direct") && sourceUdfs.TryGetValue("udf_direct", out var directVal))
            payload["udf_direct"] = ParseUdfDirect(directVal);
    }

    private static object? ParseUdfDirect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (bool.TryParse(value, out var b))
            return b;
        if (value is "1" or "Y" or "T")
            return true;
        if (value is "0" or "N" or "F")
            return false;
        return null;
    }

    public static string SerializePayload(Dictionary<string, object?> payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

    private static bool TryParseUoms(Dictionary<string, string> fields, out List<StockItemUom> uoms)
    {
        uoms = [];
        if (!fields.TryGetValue("sdsuom_json", out var json) || string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            var list = JsonSerializer.Deserialize<List<StockItemUom>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (list is null || list.Count == 0)
                return false;
            uoms = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseCategories(Dictionary<string, string> fields, out List<StockItemCategory> cats)
    {
        cats = [];
        if (!fields.TryGetValue("sdscategory_json", out var json) || string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            var list = JsonSerializer.Deserialize<List<StockItemCategory>>(json);
            if (list is null || list.Count == 0)
                return false;
            cats = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string First(Dictionary<string, string> fields, string? primary, params string[] keys)
    {
        if (!string.IsNullOrWhiteSpace(primary))
            return primary.Trim();
        return Field(fields, keys);
    }

    private static string Field(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return "";
    }

    private static string FieldOr(Dictionary<string, string> fields, string fallback, params string[] keys)
    {
        var s = Field(fields, keys);
        return s.Length > 0 ? s : fallback;
    }

    private static bool Bool(Dictionary<string, string> fields, bool fallback, params string[] keys)
    {
        var s = Field(fields, keys);
        if (s.Length == 0) return fallback;
        if (bool.TryParse(s, out var b)) return b;
        if (s is "1" or "Y" or "T") return true;
        if (s is "0" or "N" or "F") return false;
        return fallback;
    }

    private static int Int(Dictionary<string, string> fields, int fallback, params string[] keys)
    {
        var s = Field(fields, keys);
        return int.TryParse(s, out var n) ? n : fallback;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeCreationDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (DateTime.TryParse(trimmed, out var dt))
            return dt.ToString("yyyy-MM-dd");
        return trimmed.Length >= 10 ? trimmed[..10] : trimmed;
    }

    private static void NormalizeUoms(StockItem item)
    {
        if (item.Uoms.Count == 0)
        {
            var uom = string.IsNullOrWhiteSpace(item.Uom) ? "PCS" : item.Uom.Trim();
            item.Uom = uom;
            if (string.IsNullOrWhiteSpace(item.Suom))
                item.Suom = uom;
            item.Uoms = [new StockItemUom { Uom = uom, Rate = "1", RefCost = item.RefCost, RefPrice = item.RefPrice, IsBase = true }];
            return;
        }

        if (!item.Uoms.Any(u => u.IsBase))
            item.Uoms[0].IsBase = true;

        var baseCount = item.Uoms.Count(u => u.IsBase);
        if (baseCount > 1)
        {
            var first = true;
            foreach (var u in item.Uoms)
            {
                if (!u.IsBase) continue;
                if (first) { first = false; continue; }
                u.IsBase = false;
            }
        }

        var baseUom = item.Uoms.First(u => u.IsBase);
        var baseCode = (baseUom.Uom ?? "").Trim();
        if (baseCode.Length > 0)
        {
            item.Uom = baseCode;
            if (string.IsNullOrWhiteSpace(item.Suom))
                item.Suom = baseCode;
        }
    }
}
