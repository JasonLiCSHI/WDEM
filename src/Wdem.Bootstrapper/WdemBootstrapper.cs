using Autofac;

namespace Wdem.Bootstrapper;

public static class WdemBootstrapper
{
  public static WdemSession StartSession(string component) =>
      StartSession(new WdemBootstrapperOptions(component));

  public static WdemSession StartSession(WdemBootstrapperOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);

    var builder = new ContainerBuilder();
    builder.RegisterModule(new WdemModule(options));
    return new WdemSession(builder.Build());
  }
}
