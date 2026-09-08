using FirebirdSql.Data.FirebirdClient;
var csb = new FbConnectionStringBuilder { DataSource = "localhost", Port = 3050, UserID = "SYSDBA", Password = "masterkey", Charset = "UTF8", Dialect = 3 };
async Task<string> Path(string db) {
    var c = new FbConnectionStringBuilder { DataSource = "localhost", Port = 3050, Database = "C:/SQLData/SqlAccountSync/SYNC.FDB", UserID = "SYSDBA", Password = "masterkey", Charset = "UTF8", Dialect = 3 }.ToString();
    await using var conn = new FbConnection(c); await conn.OpenAsync();
    await using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT FBPATH FROM SYNC_DATABASE WHERE DBCODE=@C"; cmd.Parameters.AddWithValue("@C", db);
    return (string)(await cmd.ExecuteScalarAsync() ?? "");
}
var s2 = await Path("S2"); csb.Database = s2;
await using var conn2 = new FbConnection(csb.ToString()); await conn2.OpenAsync();
await using var cmd2 = conn2.CreateCommand();
cmd2.CommandText = "SELECT TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS WHERE RDB$SYSTEM_FLAG=0 AND RDB$RELATION_NAME STARTING WITH 'ST_' ORDER BY 1";
await using var r = await cmd2.ExecuteReaderAsync();
while (await r.ReadAsync()) Console.WriteLine(r.GetString(0).Trim());
