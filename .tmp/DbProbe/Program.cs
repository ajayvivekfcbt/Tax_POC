using Microsoft.Data.Sqlite;
using System.Diagnostics;

var dbPath = @"C:\Users\apillai0418\Desktop\TAX_POC\Tax_POC\tax_reporting_local.db";
var cs = $"Data Source={dbPath}";

using var conn = new SqliteConnection(cs);
conn.Open();

using var countCmd = conn.CreateCommand();
countCmd.CommandText = "SELECT COUNT(*) FROM TaxDetails";
var totalCount = countCmd.ExecuteScalar();
Console.WriteLine($"Total TaxDetails records in SQLite: {totalCount}");
Console.WriteLine();

var taxYear = "2025";
var form = "1098";

Console.WriteLine($"=== Timing detail summary queries for TaxYear={taxYear}, Form={form} ===");

using var summaryCountCmd = conn.CreateCommand();
summaryCountCmd.CommandText = @"
SELECT COUNT(*)
FROM TaxDetails
WHERE TaxYear = $taxYear AND Form = $form";
summaryCountCmd.Parameters.AddWithValue("$taxYear", taxYear);
summaryCountCmd.Parameters.AddWithValue("$form", form);

var sw = Stopwatch.StartNew();
var formCount = summaryCountCmd.ExecuteScalar();
sw.Stop();
Console.WriteLine($"Filtered count: {formCount} in {sw.ElapsedMilliseconds} ms");

using var summaryAssocCmd = conn.CreateCommand();
summaryAssocCmd.CommandText = @"
SELECT Asa, COUNT(*) AS RecordCount
FROM TaxDetails
WHERE TaxYear = $taxYear AND Form = $form
GROUP BY Asa
ORDER BY Asa";
summaryAssocCmd.Parameters.AddWithValue("$taxYear", taxYear);
summaryAssocCmd.Parameters.AddWithValue("$form", form);

sw.Restart();
using var summaryReader = summaryAssocCmd.ExecuteReader();
var rows = 0;
while (summaryReader.Read())
{
    rows++;
    if (rows <= 10)
    {
        Console.WriteLine($"  {summaryReader["Asa"]}: {summaryReader["RecordCount"]}");
    }
}
sw.Stop();
Console.WriteLine($"Association summary rows: {rows} in {sw.ElapsedMilliseconds} ms");

using var pageCmd = conn.CreateCommand();
pageCmd.CommandText = @"
SELECT Asa, MbrNo, MbrSub
FROM TaxDetails
WHERE TaxYear = $taxYear AND Form = $form
ORDER BY Asa, MbrNo, MbrSub
LIMIT 50";
pageCmd.Parameters.AddWithValue("$taxYear", taxYear);
pageCmd.Parameters.AddWithValue("$form", form);

sw.Restart();
using var pageReader = pageCmd.ExecuteReader();
var pageRows = 0;
while (pageReader.Read())
{
    pageRows++;
}
sw.Stop();
Console.WriteLine($"First page rows: {pageRows} in {sw.ElapsedMilliseconds} ms");

conn.Close();
