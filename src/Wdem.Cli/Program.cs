using Wdem.Cli;

try
{
  var profilesDirectory = Path.GetFullPath(
      Path.Combine(Directory.GetCurrentDirectory(), "profiles"));
  var handler = await WdemCommandHandler.CreateAsync(profilesDirectory);
  return await WdemCliBuilder.Build(handler).Parse(args).InvokeAsync();
}
catch (OperationCanceledException exception)
{
  await WdemCommandHandler.WriteExceptionEventAsync(
      exception,
      args.Contains("--json", StringComparer.Ordinal),
      cancelled: true,
      Console.Error);
  return 130;
}
catch (Exception exception)
{
  await WdemCommandHandler.WriteExceptionEventAsync(
      exception,
      args.Contains("--json", StringComparer.Ordinal),
      cancelled: false,
      Console.Error);
  return 1;
}
