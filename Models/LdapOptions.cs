namespace Tx9501.Models;

public sealed class LdapOptions
{
    public bool Enabled { get; set; } = false;
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 389;
    public bool UseSsl { get; set; }
    public bool SkipCertificateValidation { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string UserDnPattern { get; set; } = string.Empty;
    public string SearchBaseDn { get; set; } = string.Empty;
    public string SearchFilterTemplate { get; set; } = "(sAMAccountName={0})";
}
