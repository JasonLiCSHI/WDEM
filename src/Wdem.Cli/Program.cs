using Wdem.Cli;
using Wdem.Core.Runs;

var redactor = new LogRedactor();
var runEvents = new RunEventHub();
return await WdemCliHost.RunAsync(
    args,
    async cancellationToken =>
    {
      var profilesDirectory = Path.GetFullPath(
          Path.Combine(Directory.GetCurrentDirectory(), "profiles"));
      return await WdemCommandHandler.CreateAsync(
          profilesDirectory,
          output: Console.Out,
          error: Console.Error,
          cancellationToken: cancellationToken,
          redactor: redactor,
          eventSink: runEvents);
    },
    Console.Out,
    Console.Error,
    redactor: redactor);
