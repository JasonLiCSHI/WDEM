using System.Globalization;
using System.Windows;
using Microsoft.Win32;

namespace Wdem.App;

internal static class I18n
{
  public static bool IsChinese { get; private set; }

  public static void Initialize(ResourceDictionary resources)
  {
    using var languageKey = Registry.CurrentUser.OpenSubKey(@"Software\WDEM");
    var installedLanguage = languageKey?.GetValue("Language") as string;
    IsChinese = installedLanguage switch
    {
      "english" => false,
      "chinesesimplified" => true,
      _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
          "zh",
          StringComparison.OrdinalIgnoreCase)
    };

    var cultureName = IsChinese ? "zh-CN" : "en-US";
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
    resources.MergedDictionaries.Add(new ResourceDictionary
    {
      Source = new Uri($"Resources/Strings.{cultureName}.xaml", UriKind.Relative)
    });
  }

  public static string Get(string key) =>
      Application.Current.TryFindResource(key) as string ?? key;

  public static string Format(string key, params object?[] arguments) =>
      string.Format(CultureInfo.CurrentUICulture, Get(key), arguments);
}
