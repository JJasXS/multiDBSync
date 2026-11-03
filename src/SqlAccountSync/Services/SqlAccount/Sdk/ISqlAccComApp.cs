namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>Abstraction over SQLAcc.BizApp so unit tests never create real COM objects.</summary>
public interface ISqlAccComApp : IDisposable
{
    bool IsLogin { get; }
    void Login(string userName, string password, string dcfPath, string databaseFileName);
    void Logout();

    /// <summary>Optional sanitized COM diagnostics (empty on fakes). Never contains secrets.</summary>
    string LastComDiagnostics => "";

    /// <summary>
    /// Runs SQL via <c>DBManager.NewDataSet</c> (wiki Read pattern) and returns <c>RecordCount</c>.
    /// Caller must pass SELECT/WITH only.
    /// </summary>
    int QueryRecordCount(string selectSql);

    /// <summary>
    /// CHM application_objects4: enumerate <c>BizObjects.Items(i)</c>. Read-only metadata; no Open/New/Save.
    /// </summary>
    IReadOnlyList<string> ListBizObjectNames() => Array.Empty<string>();

    /// <summary>
    /// CHM application_objects4: Find + DataSets + Fields + XMLFieldDefs. No New/Edit/Save/Delete/Post/Open.
    /// </summary>
    SqlAccBizObjectMetadata InspectBizObject(string name) =>
        new(name, Found: false, FindError: "Not implemented on this fake/COM host.", DataSets: [], XmlFieldDefs: "");

    /// <summary>Returns true if Agent CODE exists (wiki FindKeyByRef / CHM Params+Open pattern).</summary>
    bool AgentExists(string code);

    /// <summary>CHM Add Agent: New + Code/Description + Save.</summary>
    void AgentCreate(string code, string description);

    /// <summary>CHM Edit Agent: Params Code, Open, Edit, set fields, Save, Close.</summary>
    void AgentUpdate(string code, string description);

    /// <summary>Reads Description for an existing Agent CODE (Params+Open).</summary>
    string AgentReadDescription(string code);

    /// <summary>CHM Delete Agent: Params Code, Open, Delete, Close.</summary>
    void AgentDelete(string code);
}

public interface ISqlAccComAppFactory
{
    ISqlAccComApp Create();
}
