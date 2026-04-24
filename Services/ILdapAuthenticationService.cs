namespace Tx9501.Services;

public sealed record LdapAuthResult(bool Succeeded, string ErrorMessage, string DisplayName);

public interface ILdapAuthenticationService
{
    Task<LdapAuthResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
}
