using System.Text.Json;
using System.Text.Json.Serialization;
using Susurro.Core.Config;
using Susurro.Core.Updates;

namespace Susurro.Core.Protocol;

// Serialización con generación de código (sin reflexión): menor tiempo de arranque y memoria.

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Packet))]
[JsonSerializable(typeof(DiscoveryPacket))]
internal partial class WireJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(UpdateManifest))]
internal partial class UpdateJsonContext : JsonSerializerContext
{
}

public static class SusurroJson
{
    internal static WireJsonContext Wire => WireJsonContext.Default;
    internal static SettingsJsonContext Settings => SettingsJsonContext.Default;
    internal static UpdateJsonContext Update => UpdateJsonContext.Default;

    public static byte[] Serialize(Packet p) => JsonSerializer.SerializeToUtf8Bytes(p, WireJsonContext.Default.Packet);

    /// <summary>Deserializa un paquete. Lanza <see cref="ProtocolException"/> si es inválido.</summary>
    public static Packet Deserialize(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var p = JsonSerializer.Deserialize(utf8, WireJsonContext.Default.Packet);
            if (p == null || string.IsNullOrEmpty(p.T)) throw new ProtocolException("Paquete sin tipo");
            return p;
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("JSON inválido: " + ex.Message);
        }
    }

    public static byte[] Serialize(DiscoveryPacket p) => JsonSerializer.SerializeToUtf8Bytes(p, WireJsonContext.Default.DiscoveryPacket);

    public static DiscoveryPacket? TryDeserializeDiscovery(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var p = JsonSerializer.Deserialize(utf8, WireJsonContext.Default.DiscoveryPacket);
            return p is { S: DiscoveryPacket.Magic } && !string.IsNullOrEmpty(p.Id) ? p : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
