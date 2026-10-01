using SqlAccountSync.Helpers;
using SqlAccountSync.Models;
using SqlAccountSync.Services.SqlAccount;
using SqlAccountSync.Services.Sync;

namespace SqlAccountSync.Services.Accounts;

public sealed class AccountEditInput
{
    public string Code { get; init; } = "";
    public string Description { get; init; } = "";
    public string Description2 { get; init; } = "";
    public string AccType { get; init; } = "";
    public string SpecialAccType { get; init; } = "";
    public string Tax { get; init; } = "";
    public int CashFlowType { get; init; }
    public string Sic { get; init; } = "";
}

public static class AccountChartEdit
{
    public static async Task<FlashToastModel> SaveAsync(
        ISqlAccountReadAdapter read,
        ISqlAccountWriteAdapter write,
        ISyncOrchestrator sync,
        RegisteredDatabase source,
        IReadOnlyList<RegisteredDatabase> subs,
        AccountEditInput input,
        string user)
    {
        var code = (input.Code ?? "").Trim();
        if (code.Length == 0)
            return FlashToasts.Fail("Account code is required.");

        var chart = await ReadChartAsync(read, source);
        if (chart.Error is not null)
            return FlashToasts.Fail(chart.Error);

        var row = chart.Rows.FirstOrDefault(a =>
            string.Equals((a.Code ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return FlashToasts.Fail($"Account {code} was not found on master.");

        var failed = await UpdateMasterAsync(write, source, input, row, chart.Rows, code);
        if (failed is not null)
            return failed;

        return await QueueUpdatesAsync(sync, source.DbId, subs, code, user);
    }

    private static async Task<(List<GlAccountRow> Rows, string? Error)> ReadChartAsync(
        ISqlAccountReadAdapter read,
        RegisteredDatabase source)
    {
        try
        {
            return (await read.GetGlChartAsync(source), null);
        }
        catch (Exception ex)
        {
            return ([], $"Could not read master chart: {ex.Message}");
        }
    }

    private static async Task<FlashToastModel?> UpdateMasterAsync(
        ISqlAccountWriteAdapter write,
        RegisteredDatabase source,
        AccountEditInput input,
        GlAccountRow row,
        List<GlAccountRow> chart,
        string code)
    {
        var byKey = GlAccountComparer.IndexByDocKey(chart);
        var record = Apply(GlAccountMapper.ToMasterRecord(row, byKey), input);
        var parentCode = GlAccountMapper.ParentCodeOf(record);
        var destParent = GlAccountMapper.ResolveDestParentDocKey(parentCode, chart);
        var result = await write.UpdateMasterAsync(
            source, EntityCodes.Account, record, $"UI-EDIT-{code}", row.DocKey, destParent);
        return result.Success ? null : FlashToasts.Fail(result.Message);
    }

    private static async Task<FlashToastModel> QueueUpdatesAsync(
        ISyncOrchestrator sync,
        int sourceDbId,
        IReadOnlyList<RegisteredDatabase> subs,
        string code,
        string user)
    {
        var queued = 0;
        var failed = 0;
        var messages = new List<string> { $"Master {code} updated." };
        foreach (var sub in subs.Where(PartnerAutoPushTargets.HasApiCredentials))
        {
            var (jobId, msg) = await sync.EnqueueMasterUpdateAsync(
                sourceDbId, sub.DbId, EntityCodes.Account, code, user);
            FlashToasts.TallyJob(ref queued, ref failed, messages, jobId,
                $"{code} → {sub.DisplayName} (job #{jobId})",
                $"{code} → {sub.DisplayName}: {msg}");
        }

        return FlashToasts.Queue(queued, failed, messages, "update");
    }

    private static MasterRecord Apply(MasterRecord record, AccountEditInput input)
    {
        record.Description = (input.Description ?? "").Trim();
        record.Fields[GlAccountMapper.FieldDescription2] = (input.Description2 ?? "").Trim();
        record.Fields[GlAccountMapper.FieldAccType] = (input.AccType ?? "").Trim();
        record.Fields[GlAccountMapper.FieldSpecialAccType] = (input.SpecialAccType ?? "").Trim();
        record.Fields[GlAccountMapper.FieldTax] = (input.Tax ?? "").Trim();
        record.Fields[GlAccountMapper.FieldCashFlowType] = input.CashFlowType.ToString();
        record.Fields[GlAccountMapper.FieldSic] = (input.Sic ?? "").Trim();
        return record;
    }
}
