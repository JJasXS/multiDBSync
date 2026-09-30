using SqlAccountSync.Authorization;

namespace SqlAccountSync.Models;

public sealed class RegisteredDatabase
{
    public int DbId { get; set; }
    public string DbCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string DbRole { get; set; } = DatabaseRoles.Master;
    public string FbHost { get; set; } = "";
    public int FbPort { get; set; } = 3050;
    public string FbPath { get; set; } = "";
    public string FbCharset { get; set; } = "UTF8";
    public int FbDialect { get; set; } = 3;
    public string FbUser { get; set; } = "SYSDBA";
    public string FbCredRef { get; set; } = "masterkey";
    public string ApiBaseUrl { get; set; } = "";
    public string ApiCredRef { get; set; } = "";
    public string Environment { get; set; } = AppEnvironments.Development;
    public bool IsActive { get; set; } = true;
    public string ConnStatus { get; set; } = ConnectionResults.Offline;
    public DateTime? LastConnOk { get; set; }
    public DateTime? LastSyncOk { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class DatabaseLink
{
    public int LinkId { get; set; }
    public int MasterDbId { get; set; }
    public int SubDbId { get; set; }
    public bool IsActive { get; set; } = true;
    public string MasterName { get; set; } = "";
    public string SubName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class EntityMapping
{
    public int MappingId { get; set; }
    public int MasterDbId { get; set; }
    public int SubDbId { get; set; }
    public string EntityCode { get; set; } = "";
    public string SrcEntityCode { get; set; } = "";
    public string DstEntityCode { get; set; } = "";
    public string MasterCode { get; set; } = "";
    public string MasterKey { get; set; } = "";
    public string SubCode { get; set; } = "";
    public string SubKey { get; set; } = "";
    public string MasterDesc { get; set; } = "";
    public string SubDesc { get; set; } = "";
    public string Status { get; set; } = MappingStatuses.Mapped;
    public int? OriginJobId { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public sealed class SyncRule
{
    public int RuleId { get; set; }
    public int LinkId { get; set; }
    public string EntityCode { get; set; } = "";
    public bool Enabled { get; set; }
    public string Direction { get; set; } = "MASTER_TO_SUB";
    public string Mode { get; set; } = "MANUAL";
    public string ConflictStrategy { get; set; } = "REQUIRE_ADMIN";
    public string ScheduleCron { get; set; } = "";
    public DateTime? LastSyncAt { get; set; }
}

public sealed class SyncRuleScope
{
    public int ScopeId { get; set; }
    public int RuleId { get; set; }
    public string RecordCode { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed class SyncJob
{
    public int JobId { get; set; }
    public string JobKind { get; set; } = "";
    public int? TxnId { get; set; }
    public int SourceDbId { get; set; }
    public int DestDbId { get; set; }
    public string EntityCode { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string Status { get; set; } = JobStatuses.Pending;
    public int Priority { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime NextRunAt { get; set; }
    public string LockedBy { get; set; } = "";
    public DateTime? LockedAt { get; set; }
    public bool DryRun { get; set; }
    public string IdemKey { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
}

public sealed class SyncTransaction
{
    public int TxnPk { get; set; }
    public string TxnId { get; set; } = "";
    public string TxnTypeCode { get; set; } = "";
    public int SourceDbId { get; set; }
    public int DestDbId { get; set; }
    public string SrcDocType { get; set; } = "";
    public string SrcDocKey { get; set; } = "";
    public string SrcDocNo { get; set; } = "";
    public string DstDocType { get; set; } = "";
    public string DstDocKey { get; set; } = "";
    public string DstDocNo { get; set; } = "";
    public string IdemKey { get; set; } = "";
    public string Status { get; set; } = JobStatuses.Pending;
    public string ErrorMessage { get; set; } = "";
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public string SourceName { get; set; } = "";
    public string DestName { get; set; } = "";
}

public sealed class TxnEvent
{
    public int EventId { get; set; }
    public int TxnPk { get; set; }
    public DateTime AtUtc { get; set; }
    public string Step { get; set; } = "";
    public string Result { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class AuditEntry
{
    public int AuditId { get; set; }
    public DateTime AtUtc { get; set; }
    public string UserEmail { get; set; } = "";
    public string Action { get; set; } = "";
    public int? DbId { get; set; }
    public string Entity { get; set; } = "";
    public string RecordId { get; set; } = "";
    public string Result { get; set; } = "";
    public string Ip { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string BeforeJson { get; set; } = "";
    public string AfterJson { get; set; } = "";
}

public sealed class AuditListItem
{
    public AuditEntry Entry { get; set; } = new();
    public string DisplayResult { get; set; } = "";
    public string? StatusDetail { get; set; }
    public int? JobId { get; set; }
    public int? TxnPk { get; set; }
    public string? LinkUrl { get; set; }
    public string LinkLabel { get; set; } = "";
}

public sealed class ErrorEntry
{
    public int ErrorId { get; set; }
    public DateTime AtUtc { get; set; }
    public int? TxnPk { get; set; }
    public int? DbId { get; set; }
    public string ErrorType { get; set; } = "";
    public string UserMessage { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public int RetryCount { get; set; }
    public bool Resolved { get; set; }
}

public sealed class AppUser
{
    public int UserId { get; set; }
    public string Code { get; set; } = "";
    public string Email { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = AppRoles.Viewer;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public sealed class UserDbAcl
{
    public int AclId { get; set; }
    public int UserId { get; set; }
    public int DbId { get; set; }
    public bool CanView { get; set; } = true;
    public bool CanSync { get; set; }
}

public sealed class MasterRecord
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string Key { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DocumentHeader
{
    public string DocType { get; set; } = "";
    public string DocKey { get; set; } = "";
    public string DocNo { get; set; } = "";
    public DateTime? DocDate { get; set; }
    public DateTime? PostDate { get; set; }
    public DateTime? TaxDate { get; set; }
    public DateTime? DueDate { get; set; }
    public string PartyCode { get; set; } = "";
    public string PartyName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Terms { get; set; } = "";
    public string Area { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Project { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "----";
    public string CurrencyRate { get; set; } = "1";
    public string Note { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DocumentLine> Lines { get; set; } = [];
}

public sealed class DocumentLine
{
    public string DtlKey { get; set; } = "";
    public int Seq { get; set; }
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Description2 { get; set; } = "";
    public decimal Qty { get; set; }
    public string Uom { get; set; } = "";
    public string Rate { get; set; } = "1";
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string TaxCode { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public string FromLocation { get; set; } = "";
    public string ToLocation { get; set; } = "";
    public string Batch { get; set; } = "";
    public string Project { get; set; } = "";
    public string Account { get; set; } = "";
    public string Disc { get; set; } = "";
    public bool TaxInclusive { get; set; }
    public string IrbmClassification { get; set; } = "";
    public DateTime? DeliveryDate { get; set; }
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DocumentListItem
{
    public string DocType { get; set; } = "";
    public string DocKey { get; set; } = "";
    public string DocNo { get; set; } = "";
    public DateTime? DocDate { get; set; }
    public string PartyCode { get; set; } = "";
    public string PartyName { get; set; } = "";
    public decimal Amount { get; set; }
    /// <summary>Sum of line quantities (useful for stock transfers where Amount is usually 0).</summary>
    public decimal Qty { get; set; }
}

public sealed class ConnectionTestResult
{
    public string Status { get; set; } = ConnectionResults.UnknownError;
    public string Message { get; set; } = "";
    public IReadOnlyList<string> MissingTables { get; set; } = [];
    public bool Success => Status == ConnectionResults.Online;
}

public sealed class ValidationResult
{
    public bool Ok { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public static ValidationResult Pass() => new() { Ok = true };
    public static ValidationResult Fail(params string[] errors) => new() { Ok = false, Errors = [.. errors] };
}

public sealed class DryRunResult
{
    public bool WouldSucceed { get; set; }
    public List<DryRunCheck> Checks { get; set; } = [];
    public string Summary { get; set; } = "";
}

public sealed class DryRunCheck
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class FlashToastModel
{
    public string Title { get; set; } = "";
    public List<string> Lines { get; set; } = [];
    public bool Ok { get; set; } = true;
}

public sealed class WriteResult
{
    public bool Success { get; set; }
    public string DocKey { get; set; } = "";
    public string DocNo { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class SchemaDiscoveryReport
{
    public int DbId { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public List<DiscoveredTable> Tables { get; set; } = [];
    public Dictionary<string, string> EntityTableHints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DiscoveredTable
{
    public string Name { get; set; } = "";
    public List<string> Columns { get; set; } = [];
}

public sealed class ReconcileResult
{
    public bool DestinationExists { get; set; }
    public string DocKey { get; set; } = "";
    public string DocNo { get; set; } = "";
    public string Source { get; set; } = "";
}
