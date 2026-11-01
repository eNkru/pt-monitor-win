namespace PTMonitor.Core.Client;

/// <summary>
/// Composes the editor's notice line. Pure so the selftest can assert the
/// combination rules without a window.
/// </summary>
public static class EditorNotices
{
    /// <summary>Shown while HTTPS is off and a password is entered.</summary>
    public const string PlaintextWarning =
        "The password is sent unencrypted — enable \"Use HTTPS\" unless the server is on a network you trust.";

    /// <summary>
    /// The thumbprint to show after a successful Test Connection, or null.
    /// Only insecure mode records one: in strict mode the certificate either
    /// validates or the connection never succeeds.
    /// </summary>
    public static string? AcceptedCertificate(bool allowInsecureTls, string? thumbprint) =>
        allowInsecureTls && !string.IsNullOrWhiteSpace(thumbprint) ? thumbprint.ToUpperInvariant() : null;

    /// <summary>
    /// The validator's notice first, then the plaintext warning. The warning is
    /// independent of validation: it appears while the form is still invalid.
    /// </summary>
    public static IReadOnlyList<string> Compose(IReadOnlyList<string>? validatorNotices, bool useTls, string? password)
    {
        var parts = new List<string>((validatorNotices?.Count ?? 0) + 1);
        if (validatorNotices is not null)
            parts.AddRange(validatorNotices.Where(notice => !string.IsNullOrEmpty(notice)));
        if (!useTls && !string.IsNullOrEmpty(password)) parts.Add(PlaintextWarning);
        return parts;
    }
}
