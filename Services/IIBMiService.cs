using Tx9501.Models;

namespace Tx9501.Services;

/// <summary>
/// Abstraction over IBM i data access.
/// Allows the controller to remain testable and the underlying
/// driver (ODBC / IBM.Data.DB2.iSeries) to be swapped independently.
/// </summary>
public interface IIBMiService
{
    /// <summary>
    /// Retrieve the TXRCTL record for the given tax year from IBM i DB2.
    /// Equivalent to reading the file after the TX9500 CALL in tx9501cl.clp.
    /// Returns null when the year is not found.
    /// </summary>
    Task<TaxControlRecord?> GetTaxControlAsync(string taxYear);

    /// <summary>
    /// Call an IBM i program (e.g. TX9505, TX9510, TX9515 …) with positional
    /// character parameters, mirroring the CALL statements in tx9501cl.clp.
    /// </summary>
    Task ExecuteProgramAsync(string programName, params string[] parameters);
}
