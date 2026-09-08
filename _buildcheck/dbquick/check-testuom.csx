using FirebirdSql.Data.FirebirdClient;
var path = @"C:\eStream\SQLAccounting\DB\sub2.fdb";
var csb = new FbConnectionStringBuilder { DataSource="localhost", Port=3050, Database=path, UserID="SYSDBA", Password="masterkey", Charset="UTF8" };
await using var conn = new FbConnection(csb.ToString()); await conn.OpenAsync();
foreach (var code in new[]{"TEST-UOM","Duckies","Dogs"})
{
  await using var cmd = new FbCommand("SELECT COUNT(*) FROM ST_ITEM WHERE UPPER(TRIM(CODE))=UPPER(@C)", conn);
  cmd.Parameters.AddWithValue("@C", code);
  Console.WriteLine($"{code}: {await cmd.ExecuteScalarAsync()}");
}
await using var uom = new FbCommand("SELECT CODE, UOM FROM ST_ITEM_UOM WHERE UPPER(TRIM(CODE)) IN ('TEST-UOM','DOGS')", conn);
await using var r = await uom.ExecuteReaderAsync();
while (await r.ReadAsync()) Console.WriteLine($"ST_ITEM_UOM {r[0]} / {r[1]}");
