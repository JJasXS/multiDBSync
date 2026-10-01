using FirebirdSql.Data.FirebirdClient;
var cs = "DataSource=localhost;Port=3050;Database=C:/eStream/SQLAccounting/DB/ACC-UAMAT.FDB;User=SYSDBA;Password=masterkey;Charset=UTF8";
await using var conn = new FbConnection(cs);
await conn.OpenAsync();
await using var cmd = new FbCommand("SELECT DOCKEY FROM PH_PO WHERE TRIM(DOCNO)='PO-00010'", conn);
Console.WriteLine(await cmd.ExecuteScalarAsync());
