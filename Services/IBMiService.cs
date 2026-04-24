using System.Data;
using System.Data.Odbc;
using Tx9501.Models;

namespace Tx9501.Services;

/// <summary>
/// Connects to IBM i DB2 via the IBM i Access ODBC Driver (Windows).
///
/// Driver requirement
/// ──────────────────
/// Install IBM i Access Client Solutions from:
///   https://www.ibm.com/support/pages/ibm-i-access-client-solutions
/// After installation the ODBC driver name is:
///   "IBM i Access ODBC Driver"
///
/// Connection string (in appsettings.json → ConnectionStrings → IBMi):
///   Driver={IBM i Access ODBC Driver};System=&lt;HOST&gt;;UID=&lt;USER&gt;;PWD=&lt;PWD&gt;;
///   DefaultLibraries=TXLIB;CommitMode=0;Naming=1
///
/// Naming=1 activates system naming (/), CommitMode=0 disables journal
/// commitment (equivalent to COMMIT(*NONE) in IBM i programs).
/// </summary>
public sealed class IBMiService : IIBMiService
{
    private readonly string _connectionString;
    private readonly string _library;
    private readonly ILogger<IBMiService> _logger;

    public IBMiService(IConfiguration configuration, ILogger<IBMiService> logger)
    {
        _connectionString = configuration.GetConnectionString("IBMi")
            ?? throw new InvalidOperationException(
                "Connection string 'IBMi' is not configured in appsettings.json.");

        _library = configuration["IBMiSettings:Library"] ?? "TXLIB";
        _logger  = logger;
    }

    // -----------------------------------------------------------------------
    // IIBMiService implementation
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<TaxControlRecord?> GetTaxControlAsync(string taxYear)
    {
        // Mirrors:  %SST(&TXRCTL 1 4)  →  TAXYR
        //           %SST(&TXRCTL 5 30) →  TAXDES
        //           %SST(&TXRCTL 35 10)→  TAXSTA
        const string sql =
            "SELECT TAXYR, TAXDES, TAXSTA " +
            "FROM {0}/TXRCTL " +
            "WHERE TAXYR = ? " +
            "FETCH FIRST 1 ROW ONLY";

        try
        {
            await using var conn = new OdbcConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new OdbcCommand(
                string.Format(sql, _library), conn);
            cmd.Parameters.AddWithValue("TAXYR", int.Parse(taxYear.PadLeft(4, '0'))); // TAXYR 4S 0

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new TaxControlRecord
                {
                    TaxYear        = reader.GetString(0).Trim(),
                    TaxDescription = reader.GetString(1).Trim(),
                    TaxStatus      = reader.GetString(2).Trim()
                };
            }

            _logger.LogWarning("Tax year {Year} not found in TXRCTL.", taxYear);
            return null;
        }
        catch (OdbcException ex)
        {
            _logger.LogError(ex,
                "ODBC error reading TXRCTL for year {Year}. " +
                "Verify the IBM i Access ODBC Driver is installed and the " +
                "connection string is correct.", taxYear);
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// IBM i programs are invoked via SQL CALL through the ODBC driver.
    /// Each parameter is passed as a fixed-length CHAR value matching
    /// the IBM i program's parameter list (e.g. TX9505 expects
    /// &amp;TXRCTL LEN(57), &amp;FORMNAME LEN(9), …).
    ///
    /// Example for TX9505:
    ///   CALL TXLIB.TX9505(?, ?, ?, ?, ?, ?, ?)
    /// </remarks>
    public async Task ExecuteProgramAsync(
        string programName,
        string? libraryOverride = null,
        params string[] parameters)
    {
        if (string.IsNullOrWhiteSpace(programName))
            throw new ArgumentNullException(nameof(programName));

        // Sanitise program name: allow only alphanumeric + underscore
        // to prevent SQL injection via the program name parameter.
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                programName, @"^[A-Za-z0-9_]{1,10}$"))
        {
            throw new ArgumentException(
                $"Invalid IBM i program name: '{programName}'.", nameof(programName));
        }

        var targetLibrary = string.IsNullOrWhiteSpace(libraryOverride)
            ? _library
            : libraryOverride.Trim();

        if (!System.Text.RegularExpressions.Regex.IsMatch(
            targetLibrary, @"^[A-Za-z0-9_]{1,10}$"))
        {
            throw new ArgumentException(
            $"Invalid IBM i library name: '{targetLibrary}'.", nameof(libraryOverride));
        }

        var placeholders = string.Join(", ", parameters.Select(_ => "?"));
        var sql = $"CALL {targetLibrary}/{programName}({placeholders})";

        try
        {
            await using var conn = new OdbcConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new OdbcCommand(sql, conn);
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(new OdbcParameter
                {
                    OdbcType  = OdbcType.Char,
                    Value     = p ?? string.Empty
                });
            }

            _logger.LogInformation("Calling IBM i program: {Library}/{Program}",
                targetLibrary, programName);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (OdbcException ex)
        {
            var isObjectNotFound = ex.Message.Contains("SQL0204", StringComparison.OrdinalIgnoreCase);
            if (isObjectNotFound)
            {
                _logger.LogWarning(ex,
                    "IBM i program object not found: {Library}/{Program}. Caller may apply web fallback.",
                    targetLibrary, programName);
            }
            else
            {
                _logger.LogError(ex,
                    "ODBC error calling IBM i program {Library}/{Program}.",
                    targetLibrary, programName);
            }
            throw;
        }
    }
}
