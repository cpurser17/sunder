using System;
using Newtonsoft.Json;

/// <summary>
/// Reads and writes TileType as its name, like StringEnumConverter, but also
/// accepts the names a type used to have (TileTypeNames.Legacy) — so level
/// files and saves written before a tile type was renamed (RoomA → Treasury)
/// still load. Always writes the current name.
/// </summary>
public class TileTypeJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) =>
        objectType == typeof(TileType) || objectType == typeof(TileType?);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        switch (reader.TokenType)
        {
            case JsonToken.Null when objectType == typeof(TileType?):
                return null;
            case JsonToken.Integer:
                return (TileType)Convert.ToInt32(reader.Value);
            case JsonToken.String when TileTypeNames.TryParse((string)reader.Value, out var type):
                return type;
            default:
                throw new JsonSerializationException(
                    $"Unknown tile type '{reader.Value}' at {reader.Path}.");
        }
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        if (value == null) writer.WriteNull();
        else writer.WriteValue(((TileType)value).ToString());
    }
}
