using System.Globalization;
using System.Resources;

namespace Oscova.Agents.Host.Localization;

/// <summary>Supplies localized text to the built-in form validation attributes without changing their rules.</summary>
public static class FormValidationText
{
    private static readonly ResourceManager Common = new("Oscova.Agents.Host.Resources.CommonText", typeof(CommonText).Assembly);
    private static readonly ResourceManager Auth = new("Oscova.Agents.Host.Resources.AuthText", typeof(AuthText).Assembly);

    public static string Username => Read(Common, CommonTextKeys.Username);
    public static string FullName => Read(Common, CommonTextKeys.FullName);
    public static string Email => Read(Common, CommonTextKeys.Email);
    public static string Password => Read(Common, CommonTextKeys.Password);
    public static string ConfirmPassword => Read(Common, CommonTextKeys.ConfirmPassword);
    public static string Required => "{0}: " + Read(Common, CommonTextKeys.ValidationThisFieldRequired);
    public static string InvalidEmail => "{0}: " + Read(Common, CommonTextKeys.ValidationEnterValidEmail);
    public static string PasswordMismatch => Read(Auth, AuthTextKeys.PasswordsDoNotMatchError);

    // MinLengthAttribute formats the field name at {0} and the limit at {1}.
    public static string MinimumLength => "{0}: " + Read(Common, CommonTextKeys.ValidationMinimumLength).Replace("{0}", "{1}", StringComparison.Ordinal);

    private static string Read(ResourceManager manager, string key) => manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
