using System.Text.Json;
using System.Text.Json.Serialization;

namespace Can.Core.Domain.Results;

/// <summary>
/// <see cref="Result{TValue}"/> için JSON biçimi: <c>{"isSuccess":true,"value":...}</c> ya da
/// <c>{"isSuccess":false,"errors":[{"code":..,"description":..,"type":"NotFound","field":..}]}</c>.
/// Önbelleğe yazma (HybridCache sonuçları serileştirir) ve servisler arası taşıma içindir; HTTP yanıtları için
/// WebApi'deki <c>ToHttpResult()</c> kullanılır. <see cref="Error.Metadata"/> taşınmaz.
/// </summary>
public sealed class ResultJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => ResultTypes.IsResult(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        Type converterType = typeof(ResultJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class ResultJsonConverter<T> : JsonConverter<Result<T>>
    {
        public override Result<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Result için JSON nesnesi bekleniyordu.");

            bool isSuccess = false;
            T? value = default;
            List<Error> errors = [];

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string property = reader.GetString() ?? string.Empty;
                reader.Read();

                if (property.Equals("isSuccess", StringComparison.OrdinalIgnoreCase))
                    isSuccess = reader.GetBoolean();
                else if (property.Equals("value", StringComparison.OrdinalIgnoreCase))
                    value = JsonSerializer.Deserialize<T>(ref reader, options);
                else if (property.Equals("errors", StringComparison.OrdinalIgnoreCase))
                    errors = ReadErrors(ref reader);
                else
                    reader.Skip();
            }

            return isSuccess ? Result<T>.Success(value!) : Result<T>.Failure(errors);
        }

        public override void Write(Utf8JsonWriter writer, Result<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("isSuccess", value.IsSuccess);

            if (value.IsSuccess)
            {
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, value.Value, options);
            }
            else
            {
                writer.WriteStartArray("errors");
                foreach (Error error in value.Errors)
                {
                    writer.WriteStartObject();
                    writer.WriteString("code", error.Code);
                    writer.WriteString("description", error.Description);
                    writer.WriteString("type", error.Type.ToString());
                    if (error.Field is not null)
                        writer.WriteString("field", error.Field);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        private static List<Error> ReadErrors(ref Utf8JsonReader reader)
        {
            var errors = new List<Error>();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                string code = "unknown", description = string.Empty;
                string? field = null;
                ErrorType type = ErrorType.Failure;

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string property = reader.GetString() ?? string.Empty;
                    reader.Read();

                    switch (property.ToUpperInvariant())
                    {
                        case "CODE":
                            code = reader.GetString() ?? code;
                            break;
                        case "DESCRIPTION":
                            description = reader.GetString() ?? string.Empty;
                            break;
                        case "FIELD":
                            field = reader.GetString();
                            break;
                        case "TYPE":
                            _ = Enum.TryParse(reader.GetString(), ignoreCase: true, out type);
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }

                errors.Add(
                    type switch
                    {
                        ErrorType.Validation => Error.Validation(code, description, field),
                        ErrorType.NotFound => Error.NotFound(code, description),
                        ErrorType.Conflict => Error.Conflict(code, description),
                        ErrorType.Unauthorized => Error.Unauthorized(code, description),
                        ErrorType.Forbidden => Error.Forbidden(code, description),
                        ErrorType.Unexpected => Error.Unexpected(code, description),
                        _ => Error.Failure(code, description),
                    }
                );
            }

            return errors;
        }
    }
}
