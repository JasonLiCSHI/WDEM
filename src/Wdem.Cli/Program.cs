using Wdem.Cli;

try
{
  var profilesDirectory = Path.GetFullPath(
      Path.Combine(Directory.GetCurrentDirectory(), "profiles"));
  var handler = await WdemCommandHandler.CreateAsync(profilesDirectory);
  return await WdemCliBuilder.Build(handler).Parse(args).InvokeAsync();
}
catch (OperationCanceledException)
{
  return 130;
}
catch (Exception exception)
{
  await Console.Error.WriteLineAsync(exception.Message);
  return 1;
}
