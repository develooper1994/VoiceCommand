using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSerializable(typeof(string[]))]
internal partial class MyJsonContext : JsonSerializerContext
{
}
