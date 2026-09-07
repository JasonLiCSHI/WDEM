using Wdem.Domain.Profiles;

namespace Wdem.Infrastructure.Profiles;

public static class ProfileParser
{
  public static EnvironmentProfile Parse(string json)
  {
    var document = ProfileDocumentDeserializer.Deserialize(json);
    return ProfileDocumentMapper.Map(document);
  }
}
