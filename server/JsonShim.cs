// .NET 8 has no System.Web.Script.Serialization. This is the small part of it LAICA uses, on top of System.Text.Json, so the same
// backend source builds for Windows (.NET Framework), macOS and Linux (.NET 8). Numbers come back as int, long or double, arrays as
// object[] and objects as Dictionary<string, object>, as they did before.
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace System.Web.Script.Serialization
{
    public sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; } = 2097152;
        public int RecursionLimit { get; set; } = 100;
        readonly JsonSerializerOptions options;

        public JavaScriptSerializer()
        {
            options = new JsonSerializerOptions
            {
                IncludeFields = true,
                PropertyNamingPolicy = null,
                DictionaryKeyPolicy = null,
                MaxDepth = 256,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            options.Converters.Add(new NaturalConverter());
        }

        public string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, options);
        }

        public T Deserialize<T>(string input)
        {
            if (input != null && input.Length > MaxJsonLength)
                throw new InvalidOperationException(
                    "Error during serialization or deserialization using the JSON JavaScriptSerializer. The length of the string exceeds the value set on the maxJsonLength property."
                );
            return JsonSerializer.Deserialize<T>(input, options);
        }

        public object DeserializeObject(string input)
        {
            return Deserialize<object>(input);
        }

        sealed class NaturalConverter : JsonConverter<object>
        {
            public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions o)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        return reader.GetString();
                    case JsonTokenType.True:
                        return true;
                    case JsonTokenType.False:
                        return false;
                    case JsonTokenType.Null:
                        return null;
                    case JsonTokenType.Number:
                        long l;
                        if (reader.TryGetInt64(out l))
                            return l >= int.MinValue && l <= int.MaxValue ? (object)(int)l : l;
                        return reader.GetDouble();
                    case JsonTokenType.StartArray:
                        var list = new List<object>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            list.Add(Read(ref reader, typeof(object), o));
                        return list.ToArray();
                    case JsonTokenType.StartObject:
                        var d = new Dictionary<string, object>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            string k = reader.GetString();
                            reader.Read();
                            d[k] = Read(ref reader, typeof(object), o);
                        }
                        return d;
                }
                throw new JsonException();
            }

            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions o)
            {
                if (value == null)
                {
                    writer.WriteNullValue();
                    return;
                }
                Type t = value.GetType();
                if (t == typeof(object))
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                    return;
                }
                JsonSerializer.Serialize(writer, value, t, o);
            }
        }
    }
}
