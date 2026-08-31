using System.Text.Json;
using Wdem.Windows.Logging;
using Xunit;

namespace Wdem.Windows.Tests;

public sealed class JsonLineSessionLogTests
{
  [Fact]
  public void Write_PersistsSessionMetadataAndOrderedJsonLines()
  {
    var directory = Path.Combine(Path.GetTempPath(), "Wdem.Tests", Guid.NewGuid().ToString("N"));
    string path;
    string sessionId;

    using (var log = JsonLineSessionLog.CreateInDirectory("test", directory))
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
  public void CreateInDirectory_WhenDirectoryIsUnavailable_DisablesLoggingWithoutThrowing()
  {
    var parent = Path.Combine(Path.GetTempPath(), "Wdem.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(parent);
    var filePath = Path.Combine(parent, "not-a-directory");
    File.WriteAllText(filePath, "occupied");

    using var log = JsonLineSessionLog.CreateInDirectory("test", filePath);

    Assert.False(log.IsEnabled);
    Assert.Null(log.Path);
    Assert.NotNull(log.LastError);
    log.Write("ignored", "Logging failure must not escape.");
  }
}
