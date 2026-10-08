using System.Buffers.Text;
using System.Text;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

internal static class JwtPayload
{
    /// <summary>
    /// Returns the payload of a signed, unencrypted JWT as the JSON text a client would see. It throws
    /// when the decode fails, so a garbled payload cannot pass a "does not contain" check by accident.
    /// </summary>
    public static string Read(string jwt)
    {
        string[] segments = jwt.Split('.');
        if (segments.Length != 3)
        {
            throw new ArgumentException("This is not a signed, unencrypted JWT.", nameof(jwt));
        }

        string payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(segments[1]));
        JsonDocument.Parse(payload).Dispose();
        return payload;
    }

    /// <summary>
    /// A JWT carries one audience as a string and more than one as an array. This reads either as a list.
    /// </summary>
    public static IReadOnlyList<string> ReadAudiences(string jwt)
    {
        using JsonDocument payload = JsonDocument.Parse(Read(jwt));
        JsonElement audience = payload.RootElement.GetProperty("aud");

        return audience.ValueKind switch
        {
            JsonValueKind.String => [audience.GetString()!],
            JsonValueKind.Array => audience.EnumerateArray().Select(element => element.GetString()!).ToList(),
            _ => throw new ArgumentException($"The \"aud\" claim is a {audience.ValueKind}.", nameof(jwt))
        };
    }
}
