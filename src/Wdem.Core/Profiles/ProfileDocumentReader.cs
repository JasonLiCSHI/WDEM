using System.Text;
using System.Text.Json;
using Wdem.Core.Execution;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Wdem.Core.Profiles;

internal static class ProfileDocumentReader
{
  internal const int MaxInputBytes = 1024 * 1024;
  internal const int MaxInputCharacters = 1024 * 1024;
  internal const int MaxDepth = 64;
  internal const int MaxNodes = 100_000;

  public static async Task<ProfileDocumentReadResult> ReadAsync(
      string sourcePath,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    try
    {
      if (new FileInfo(sourcePath).Length > MaxInputBytes)
      {
        cancellationToken.ThrowIfCancellationRequested();
        return Failure(sourcePath, "The profile input size exceeds the limit.",
            $"Profile inputs may not exceed {MaxInputBytes} bytes.");
      }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return FailureFromException(sourcePath, "The profile file could not be inspected.", exception);
    }

    byte[] bytes;
    try
    {
      bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return FailureFromException(sourcePath, "The profile file could not be read.", exception);
    }

    cancellationToken.ThrowIfCancellationRequested();
    if (bytes.Length > MaxInputBytes)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Failure(sourcePath, "The profile input size exceeds the limit.",
          $"Profile inputs may not exceed {MaxInputBytes} bytes.");
    }

    var extension = Path.GetExtension(sourcePath);
    JsonDocument document;
    try
    {
      if (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
      {
        var yaml = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(bytes);
        if (yaml.Length > MaxInputCharacters)
        {
          cancellationToken.ThrowIfCancellationRequested();
          return Failure(sourcePath, "The profile input size exceeds the limit.",
              $"Profile inputs may not exceed {MaxInputCharacters} characters.");
        }

        var yamlErrors = InspectYaml(yaml, sourcePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (yamlErrors.Count > 0)
        {
          return new ProfileDocumentReadResult(null, yamlErrors);
        }

        var yamlObject = new DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build()
            .Deserialize<object?>(yaml);
        cancellationToken.ThrowIfCancellationRequested();
        var json = new SerializerBuilder().JsonCompatible().Build().Serialize(yamlObject);
        if (json.Length > MaxInputCharacters || Encoding.UTF8.GetByteCount(json) > MaxInputBytes)
        {
          cancellationToken.ThrowIfCancellationRequested();
          return Failure(sourcePath, "The converted profile size exceeds the limit.",
              "The YAML-to-JSON representation exceeded the profile input quota.");
        }

        document = JsonDocument.Parse(json, JsonOptions);
      }
      else
      {
        document = JsonDocument.Parse(bytes, JsonOptions);
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (
        exception is JsonException or YamlException or DecoderFallbackException or InvalidOperationException)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return new ProfileDocumentReadResult(null, [ProfileErrorFactory.FromException(
          sourcePath,
          "The profile syntax is invalid.",
          "The parser rejected the input.",
          string.Empty,
          exception)]);
    }

    cancellationToken.ThrowIfCancellationRequested();
    var jsonErrors = InspectJson(document.RootElement, sourcePath, cancellationToken);
    if (jsonErrors.Count > 0)
    {
      cancellationToken.ThrowIfCancellationRequested();
      document.Dispose();
      return new ProfileDocumentReadResult(null, jsonErrors);
    }

    cancellationToken.ThrowIfCancellationRequested();
    return new ProfileDocumentReadResult(document, Array.Empty<StructuredError>());
  }

  private static JsonDocumentOptions JsonOptions => new()
  {
    MaxDepth = MaxDepth,
    AllowTrailingCommas = false,
    CommentHandling = JsonCommentHandling.Disallow
  };

  private static IReadOnlyList<StructuredError> InspectYaml(
      string yaml,
      string sourcePath,
      CancellationToken cancellationToken)
  {
    var events = new List<ParsingEvent>();
    var parser = new Parser(new StringReader(yaml));
    var depth = 0;
    var nodes = 0;
    while (parser.MoveNext())
    {
      cancellationToken.ThrowIfCancellationRequested();
      var current = parser.Current!;
      if (current is AnchorAlias)
      {
        return [ProfileErrorFactory.Create(sourcePath,
            "YAML aliases are not supported.",
            "Developer profiles must not contain YAML aliases or anchors.")];
      }

      if (current is NodeEvent node && !node.Anchor.IsEmpty)
      {
        return [ProfileErrorFactory.Create(sourcePath,
            "YAML anchors are not supported.",
            "Developer profiles must not contain YAML aliases or anchors.")];
      }

      if (current is MappingStart or SequenceStart)
      {
        depth++;
        if (depth > MaxDepth)
        {
          return [ProfileErrorFactory.Create(sourcePath,
              "The YAML profile nesting is too deep.",
              $"YAML nesting may not exceed {MaxDepth} levels.")];
        }
      }
      else if (current is MappingEnd or SequenceEnd)
      {
        depth--;
      }

      if (current is NodeEvent && ++nodes > MaxNodes)
      {
        return [ProfileErrorFactory.Create(sourcePath,
            "The YAML profile is too complex.",
            $"YAML profiles may not contain more than {MaxNodes} nodes.")];
      }

      events.Add(current);
    }

    var index = events.FindIndex(item => item is MappingStart or SequenceStart or Scalar);
    if (index < 0)
    {
      return Array.Empty<StructuredError>();
    }

    var errors = new List<StructuredError>();
    InspectYamlNode(events, ref index, string.Empty, sourcePath, errors);
    return errors;
  }

  private static void InspectYamlNode(
      IReadOnlyList<ParsingEvent> events,
      ref int index,
      string pointer,
      string sourcePath,
      List<StructuredError> errors)
  {
    if (index >= events.Count)
    {
      return;
    }

    if (events[index] is Scalar)
    {
      index++;
      return;
    }

    if (events[index] is SequenceStart)
    {
      index++;
      var itemIndex = 0;
      while (index < events.Count && events[index] is not SequenceEnd)
      {
        InspectYamlNode(events, ref index, $"{pointer}/{itemIndex}", sourcePath, errors);
        itemIndex++;
      }

      index++;
      return;
    }

    if (events[index] is not MappingStart)
    {
      index++;
      return;
    }

    index++;
    var keys = new HashSet<string>(StringComparer.Ordinal);
    while (index < events.Count && events[index] is not MappingEnd)
    {
      if (events[index] is not Scalar keyEvent)
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath,
            "The YAML mapping key is invalid.",
            "Developer profile mapping keys must be scalar strings.", pointer));
        return;
      }

      var key = keyEvent.Value ?? string.Empty;
      var childPointer = $"{pointer}/{ProfileValueExpander.EscapePointer(key)}";
      if (!keys.Add(key))
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath,
            "A duplicate YAML property was found.",
            $"Property '{key}' appears more than once in the same mapping.", childPointer));
      }

      index++;
      InspectYamlNode(events, ref index, childPointer, sourcePath, errors);
    }

    index++;
  }

  private static IReadOnlyList<StructuredError> InspectJson(
      JsonElement root,
      string sourcePath,
      CancellationToken cancellationToken)
  {
    var errors = new List<StructuredError>();
    var nodes = 0;
    InspectJsonNode(root, string.Empty, sourcePath, cancellationToken, errors, ref nodes);
    return errors;
  }

  private static void InspectJsonNode(
      JsonElement element,
      string pointer,
      string sourcePath,
      CancellationToken cancellationToken,
      List<StructuredError> errors,
      ref int nodes)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (++nodes > MaxNodes)
    {
      if (!errors.Any(error => error.Summary.Contains("complex", StringComparison.OrdinalIgnoreCase)))
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath,
            "The JSON profile is too complex.",
            $"JSON profiles may not contain more than {MaxNodes} values."));
      }

      return;
    }

    if (element.ValueKind == JsonValueKind.Object)
    {
      var properties = new HashSet<string>(StringComparer.Ordinal);
      foreach (var property in element.EnumerateObject())
      {
        var childPointer = $"{pointer}/{ProfileValueExpander.EscapePointer(property.Name)}";
        if (!properties.Add(property.Name))
        {
          errors.Add(ProfileErrorFactory.Create(sourcePath,
              "A duplicate JSON property was found.",
              $"Property '{property.Name}' appears more than once in the same object.", childPointer));
        }

        InspectJsonNode(
            property.Value,
            childPointer,
            sourcePath,
            cancellationToken,
            errors,
            ref nodes);
      }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
      var index = 0;
      foreach (var item in element.EnumerateArray())
      {
        InspectJsonNode(item, $"{pointer}/{index}", sourcePath, cancellationToken, errors, ref nodes);
        index++;
      }
    }
  }

  private static ProfileDocumentReadResult Failure(
      string sourcePath,
      string summary,
      string detail) =>
      new(null, [ProfileErrorFactory.Create(sourcePath, summary, detail)]);

  private static ProfileDocumentReadResult FailureFromException(
      string sourcePath,
      string summary,
      Exception exception) =>
      new(null, [ProfileErrorFactory.FromException(
          sourcePath,
          summary,
          "The file system rejected the operation.",
          string.Empty,
          exception)]);
}

internal sealed class ProfileDocumentReadResult(
    JsonDocument? document,
    IReadOnlyList<StructuredError> errors) : IDisposable
{
  public JsonDocument? Document { get; } = document;
  public IReadOnlyList<StructuredError> Errors { get; } = errors;
  public bool IsValid => Document is not null && Errors.Count == 0;

  public void Dispose() => Document?.Dispose();
}
