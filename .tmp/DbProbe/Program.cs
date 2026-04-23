using Microsoft.Data.Sqlite;

var dbPath = @"..\..\tax_reporting_local.db";
var cs = $"Data Source={dbPath}";

using var conn = new SqliteConnection(cs);
conn.Open();

// First, check count of all records
using var countCmd = conn.CreateCommand();
countCmd.CommandText = "SELECT COUNT(*) FROM TaxDetails";
var totalCount = countCmd.ExecuteScalar();
Console.WriteLine($"Total TaxDetails records in SQLite: {totalCount}");
Console.WriteLine();

// Query for member 918308
Console.WriteLine("=== Searching for member 918308 ===");
using var cmd = conn.CreateCommand();
cmd.CommandText = @"
SELECT Id, TaxYear, Form, Asa, MbrNo, MbrSub, ChangeDate, CreatedAt, UpdatedAt 
FROM TaxDetails 
WHERE MbrNo = 918308 
ORDER BY UpdatedAt DESC";

using var reader = cmd.ExecuteReader();
if (!reader.HasRows)
{
    Console.WriteLine("No records found for member 918308 in SQLite.");
}
else
{
    Console.WriteLine("Found records for member 918308:");
    Console.WriteLine(new string('-', 120));
    while (reader.Read())
    {
        var id = reader["Id"];
        var taxYear = reader["TaxYear"];
        var form = reader["Form"];
        var asa = reader["Asa"];
        var mbrNo = reader["MbrNo"];
        var mbrSub = reader["MbrSub"];
        var changeDate = reader["ChangeDate"];
        var createdAt = reader["CreatedAt"];
        var updatedAt = reader["UpdatedAt"];
        
        Console.WriteLine($"Id={id} | TaxYear={taxYear} | Form=[{form}] | Asa=[{asa}] | MbrNo={mbrNo} | MbrSub=[{mbrSub}]");
        Console.WriteLine($"  ChangeDate={changeDate} | CreatedAt={createdAt} | UpdatedAt={updatedAt}");
        Console.WriteLine();
    }
}

// Show last 5 records in table (by UpdatedAt)
Console.WriteLine("\n=== Last 5 records in TaxDetails ===");
using var recentCmd = conn.CreateCommand();
recentCmd.CommandText = @"
SELECT Id, TaxYear, Form, Asa, MbrNo, MbrSub, ChangeDate, CreatedAt, UpdatedAt 
FROM TaxDetails 
ORDER BY UpdatedAt DESC 
LIMIT 5";

using var recentReader = recentCmd.ExecuteReader();
if (!recentReader.HasRows)
{
    Console.WriteLine("No records in TaxDetails table.");
}
else
{
    while (recentReader.Read())
    {
        var id = recentReader["Id"];
        var taxYear = recentReader["TaxYear"];
        var form = recentReader["Form"];
        var asa = recentReader["Asa"];
        var mbrNo = recentReader["MbrNo"];
        var mbrSub = recentReader["MbrSub"];
        var changeDate = recentReader["ChangeDate"];
        var createdAt = recentReader["CreatedAt"];
        var updatedAt = recentReader["UpdatedAt"];
        
        Console.WriteLine($"Id={id} | MbrNo={mbrNo} | TaxYear={taxYear} | Form=[{form}]");
        Console.WriteLine($"  UpdatedAt={updatedAt}");
    }
}

conn.Close();
