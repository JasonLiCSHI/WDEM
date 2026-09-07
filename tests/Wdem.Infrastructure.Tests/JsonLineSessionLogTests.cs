using System.Text.Json;
using Wdem.Application.Logging;
using Wdem.Domain.Events;
using Wdem.Domain.Execution;
using Wdem.Infrastructure.Logging;
using Wdem.Testing;
using Xunit;

namespace Wdem.Infrastructure.Tests;

public sealed class JsonLineSessionLogTests : TemporaryDirectoryTestBase
{
  [Fact]
  public void Write_WhenEventIsRecorded_ThenPersistsSessionMetadataAndOrderedJsonLines()
  {
    string path;
    string sessionId;

    using (var log = JsonLineSessionLog.CreateInDirectory("test", RootDirectory))
    {
      Assert.True(log.IsEnabled, log.LastError);
      path = Assert.IsType<string>(log.Path);
      sessionId = log.SessionId;
      log.Write("test_event", "hello", new { value = 42 });
    }

    var lines = File.ReadAllLines(path);
    Assert.Equal(3, lines.Length);

    using var start = JsonDocument.Parse(lines[0]);
    using var item = JsonDocument.Parse(lines[1]);
    using var end = JsonDocument.Parse(lines[2]);
    Assert.Equal("session_start", start.RootElement.GetProperty("category").GetString());
    Assert.Equal("test_event", item.RootElement.GetProperty("category").GetString());
    Assert.Equal(42, item.RootElement.GetProperty("data").GetProperty("value").GetInt32());
    Assert.Equal(sessionId, item.RootElement.GetProperty("sessionId").GetString());
    Assert.Equal(2, item.RootElement.GetProperty("sequence").GetInt64());
    Assert.Equal("session_end", end.RootElement.GetProperty("category").GetString());
  }

  [Fact]
  public void CreateInDirectory_WhenDirectoryIsUnavailable_ThenDisablesLoggingWithoutThrowing()
  {
    var filePath = TestPath("not-a-directory");
    File.WriteAllText(filePath, "occupied");

    using var log = JsonLineSessionLog.CreateInDirectory("test", filePath);

    Assert.False(log.IsEnabled);
    Assert.Null(log.Path);
    Assert.NotNull(log.LastError);
    log.Write("ignored", "Logging failure must not escape.");
  }

  [Fact]
  public void WriteUserAction_WhenRecorded_ThenPersistsNonSensitiveStructuredData()
  {
    string path;

    using (var log = JsonLineSessionLog.CreateInDirectory("test", RootDirectory))
    {
      path = Assert.IsType<string>(log.Path);
      log.WriteUserAction(
          "start_task",
          UserActionOutcome.Requested,
          "csharp-developer",
          ["visual-studio-professional"]);
    }

    using var item = JsonDocument.Parse(File.ReadLines(path).ElementAt(1));
    var root = item.RootElement;
    Assert.Equal("user_action", root.GetProperty("category").GetString());
    Assert.Equal("start_task: Requested", root.GetProperty("message").GetString());
    var data = root.GetProperty("data");
    Assert.Equal("start_task", data.GetProperty("Operation").GetString());
    Assert.Equal("Requested", data.GetProperty("Outcome").GetString());
    Assert.Equal("csharp-developer", data.GetProperty("ProfileId").GetString());
    Assert.Equal(
        "visual-studio-professional",
        data.GetProperty("TaskIds")[0].GetString());
    Assert.False(data.TryGetProperty("Arguments", out _));
  }

  [Fact]
  public void DomainEventHandler_WhenTaskFinishes_ThenPersistsStructuredBusinessFact()
  {
    string path;

    using (var log = JsonLineSessionLog.CreateInDirectory("test", RootDirectory))
    {
      path = Assert.IsType<string>(log.Path);
      var handler = new DomainEventSessionLogHandler(log);
      handler.Handle(new TaskWorkflowFinished(
          "csharp-developer",
          "visual-studio-professional",
          "succeeded",
          TaskOutcome.Succeeded,
          Error: null));
    }

    using var item = JsonDocument.Parse(File.ReadLines(path).ElementAt(1));
    var root = item.RootElement;
    Assert.Equal("domain_event", root.GetProperty("category").GetString());
    Assert.Contains("visual-studio-professional", root.GetProperty("message").GetString());
    var data = root.GetProperty("data");
    Assert.Equal("csharp-developer", data.GetProperty("ProfileId").GetString());
    Assert.Equal("visual-studio-professional", data.GetProperty("TaskId").GetString());
    Assert.Equal("Succeeded", data.GetProperty("Outcome").GetString());
  }
}
