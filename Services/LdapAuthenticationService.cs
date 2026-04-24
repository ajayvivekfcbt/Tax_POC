using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Options;
using Tx9501.Models;

namespace Tx9501.Services;

public sealed class LdapAuthenticationService : ILdapAuthenticationService
{
    private readonly LdapOptions _options;
    private readonly ILogger<LdapAuthenticationService> _logger;

    public LdapAuthenticationService(
        IOptions<LdapOptions> options,
        ILogger<LdapAuthenticationService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task<LdapAuthResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(new LdapAuthResult(false, "LDAP sign-on is disabled. Set Ldap:Enabled to true.", string.Empty));
        }

        if (string.IsNullOrWhiteSpace(_options.Server))
        {
            return Task.FromResult(new LdapAuthResult(false, "LDAP server is not configured.", string.Empty));
        }

        var normalizedUser = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedUser) || string.IsNullOrWhiteSpace(password))
        {
            return Task.FromResult(new LdapAuthResult(false, "Username and password are required.", string.Empty));
        }

        try
        {
            var identifier = new LdapDirectoryIdentifier(_options.Server, _options.Port);
            using var connection = new LdapConnection(identifier)
            {
                AuthType = AuthType.Basic
            };

            connection.SessionOptions.ProtocolVersion = 3;
            connection.Timeout = TimeSpan.FromSeconds(10);
            if (_options.UseSsl)
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }

            if (_options.SkipCertificateValidation)
            {
                connection.SessionOptions.VerifyServerCertificate += (_, _) => true;
            }

            var bindUser = BuildBindUser(normalizedUser);
            connection.Bind(new NetworkCredential(bindUser, password));

            var displayName = normalizedUser;
            if (!string.IsNullOrWhiteSpace(_options.SearchBaseDn))
            {
                displayName = TryResolveDisplayName(connection, normalizedUser) ?? normalizedUser;
            }

            return Task.FromResult(new LdapAuthResult(true, string.Empty, displayName));
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "LDAP authentication failed for {User}.", normalizedUser);
            return Task.FromResult(new LdapAuthResult(false, "Invalid username or password.", string.Empty));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP authentication error for {User}.", normalizedUser);
            return Task.FromResult(new LdapAuthResult(false, "Unable to complete LDAP sign-on right now.", string.Empty));
        }
    }

    private string BuildBindUser(string username)
    {
        if (!string.IsNullOrWhiteSpace(_options.UserDnPattern))
        {
            return _options.UserDnPattern.Replace("{0}", username, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(_options.Domain) && !username.Contains('\\') && !username.Contains('@'))
        {
            return $"{_options.Domain}\\{username}";
        }

        return username;
    }

    private string? TryResolveDisplayName(LdapConnection connection, string username)
    {
        try
        {
            var escapedUser = EscapeFilterValue(username);
            var filterTemplate = string.IsNullOrWhiteSpace(_options.SearchFilterTemplate)
                ? "(sAMAccountName={0})"
                : _options.SearchFilterTemplate;
            var filter = string.Format(filterTemplate, escapedUser);
            var request = new SearchRequest(_options.SearchBaseDn, filter, SearchScope.Subtree, "displayName", "cn");
            var response = (SearchResponse)connection.SendRequest(request);
            if (response.Entries.Count == 0)
            {
                return null;
            }

            var entry = response.Entries[0];
            return entry.Attributes["displayName"]?[0]?.ToString()
                ?? entry.Attributes["cn"]?[0]?.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve LDAP display name for {User}.", username);
            return null;
        }
    }

    private static string EscapeFilterValue(string value)
    {
        return value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }
}
