namespace SqlAccountSync.Models;

public sealed class MasterSyncPayload
{
    public MasterRecord Record { get; set; } = new();
    public string SourceEntity { get; set; } = "";
    public string DestEntity { get; set; } = "";
}
