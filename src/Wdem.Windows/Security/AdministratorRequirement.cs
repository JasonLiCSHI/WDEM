using System.Security.Principal;

namespace Wdem.Windows.Security;

public static class AdministratorRequirement
{
  public const int AccessDeniedExitCode = 5;

  public static bool IsSatisfied()
  {
    if (!OperatingSystem.IsWindows())
    {
      return false;
    }

    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
  }
}
