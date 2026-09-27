using System.Buffers.Text;
using System.Text;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

internal static class JwtPayload
{
    /// <summary>
    /// Returns the payload of a signed, unencrypted JWT as the JSON text a client would see. Parsing it
    /// proves the decode worked, so a garbled payload cannot pass a "does not contain" check by accident.
    /// </summary>
    public static string Read(string jwt)
    {
        string[] segments = jwt.Split('.');
        segments.Length.ShouldBe(3);

        string payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(segments[1]));
        JsonDocument.Parse(payload).Dispose();
        return payload;
    }
}
