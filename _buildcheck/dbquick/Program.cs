using FirebirdSql.Data.FirebirdClient;

var csb = new FbConnectionStringBuilder
{
    DataSource = "localhost", Port = 3050, UserID = "SYSDBA",
    Password = Environment.GetEnvironmentVariable("CENTRAL_FB_MASTERKEY") ?? "masterkey",
    Charset = "UTF8",
    Database = @"C:\eStream\SQLAccounting\DB\MASTER.FDB"
};
await using var conn = new FbConnection(csb.ToString());
await conn.OpenAsync();
Console.WriteLine("Customers:");
await using (var c = new FbCommand("SELECT TRIM(CODE) FROM AR_CUSTOMER ORDER BY CODE", conn))
await using (var r = await c.ExecuteReaderAsync())
    while (await r.ReadAsync()) Console.WriteLine("  " + r.GetString(0));
Console.WriteLine("Suppliers:");
await using (var c2 = new FbCommand("SELECT TRIM(CODE) FROM AP_SUPPLIER ORDER BY CODE", conn))
await using (var r2 = await c2.ExecuteReaderAsync())
    while (await r2.ReadAsync()) Console.WriteLine("  " + r2.GetString(0));
