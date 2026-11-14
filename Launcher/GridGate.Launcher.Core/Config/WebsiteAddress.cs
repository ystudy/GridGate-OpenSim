using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Core.Config;

/// <summary>
/// The website's address as viewers and residents are given it: Robust's grid info (welcome, register, password) and the guest
/// Connect page's grid manager settings both use this, so they can't disagree.
/// </summary>
public static class WebsiteAddress
{
    /// <summary>"http://host:port", the host as <see cref="HostAddress.Effective"/> gives it. No trailing slash.</summary>
    public static string Base(LauncherSettings s) => $"http://{HostAddress.Effective(s.Hostname)}:{s.WebPort}";

    public static string Welcome(LauncherSettings s) => Base(s) + "/welcome";
    public static string SignUp(LauncherSettings s) => Base(s) + "/signup";
    public static string ForgotPassword(LauncherSettings s) => Base(s) + "/guest/forgot-password";

    /// <summary>
    /// The economy helper Firestorm's Buy currency and Buy land windows call (money design §8.1). Always http, and always
    /// ending in "/": Firestorm appends "currency.php" and "landtool.php" without a separator.
    /// </summary>
    public static string Economy(LauncherSettings s) => Base(s) + "/economy/";
}
