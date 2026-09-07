using System.Text;

namespace Wdem.Infrastructure.Profiles;

internal static class ProfileDocumentReader
{
  public static async Task<string> ReadAsync(
      Stream stream,
      string documentName,
      int maxDocumentBytes,
      CancellationToken cancellationToken)
  {
    using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    while (true)
    {
      var read = await stream.ReadAsync(chunk, cancellationToken);
      if (read == 0)
      {
        break;
      }

      await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
      if (buffer.Length > maxDocumentBytes)
      {
        throw TooLarge(documentName, maxDocumentBytes);
      }
    }

    return new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
  }

  public static InvalidDataException TooLarge(string documentName, int maxDocumentBytes) =>
      new($"Profile Source document '{documentName}' exceeds the {maxDocumentBytes} byte size limit.");
}
