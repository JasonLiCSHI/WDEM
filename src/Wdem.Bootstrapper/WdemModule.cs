using Autofac;
using Wdem.Application.Runtime;
using Wdem.Core.Profiles;
using Wdem.Windows.Configuration;
using Wdem.Windows.Logging;
using Wdem.Windows.Processes;
using Wdem.Windows.Runtime;

namespace Wdem.Bootstrapper;

internal sealed class WdemModule(WdemBootstrapperOptions options) : Module
{
  protected override void Load(ContainerBuilder builder)
  {
    builder.Register(_ => OpenSettings(options)).SingleInstance();
    builder.Register(context =>
        new ProfileCatalog(
            context.Resolve<WdemUserSettingsStore>().ProfileSource,
            context.Resolve<WdemUserSettingsStore>().CacheDirectory))
        .SingleInstance();
    builder.RegisterType<DefaultProcessRunner>()
        .As<IProcessRunner>()
        .SingleInstance();
    builder.Register(context =>
        new WindowsTaskRuntime(
            context.Resolve<IProcessRunner>(),
            options.ApplicationDirectory))
        .As<ITaskRuntime>()
        .SingleInstance();
    builder.Register(_ => CreateLog(options)).SingleInstance();
  }

  private static WdemUserSettingsStore OpenSettings(WdemBootstrapperOptions options)
  {
    if (options.SettingsPath is null && options.CacheDirectory is null)
    {
      return WdemUserSettingsStore.OpenDefault();
    }

    if (options.SettingsPath is null || options.CacheDirectory is null)
    {
      throw new ArgumentException(
          "SettingsPath and CacheDirectory must be supplied together.",
          nameof(options));
    }

    return WdemUserSettingsStore.Open(options.SettingsPath, options.CacheDirectory);
  }

  private static JsonLineSessionLog CreateLog(WdemBootstrapperOptions options) =>
      options.LogDirectory is null
          ? JsonLineSessionLog.Create(options.Component)
          : JsonLineSessionLog.CreateInDirectory(options.Component, options.LogDirectory);
}
