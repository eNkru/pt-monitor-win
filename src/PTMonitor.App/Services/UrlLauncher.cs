using System.Diagnostics;

namespace PTMonitor.App.Services;

/// <summary>
/// Opens a URL in the default browser. The shell decides what happens; a
/// refusal (no browser registered, an unusable scheme) is cosmetic and must
/// never take the agent down, so the failure is swallowed.
/// </summary>
public static class UrlLauncher
{
    public static bool Open(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            // An unusable host silently does nothing (the caller still works).
            return false;
        }
    }
}
