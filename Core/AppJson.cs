using System.Text.Json.Serialization;
using ZeTwitchMiner.Twitch;

namespace ZeTwitchMiner.Core;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(SessionData))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AppJson : JsonSerializerContext;
