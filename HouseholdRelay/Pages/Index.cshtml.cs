using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HouseholdRelay.Pages;

public sealed class IndexModel(ConfigurationService settings, IdentityService identities) : PageModel
{
    public int UserCount { get; private set; }
    public bool CredentialConfigured { get; private set; }
    public bool CertificateConfigured { get; private set; }
    public string PublicUrl { get; private set; } = "";
    public void OnGet()
    {
        UserCount = identities.List().Count; CredentialConfigured = settings.HasCredential;
        CertificateConfigured = settings.HasCertificate; PublicUrl = settings.Read().Value.PublicUrl;
    }
}
