using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonPad.Services;

public sealed record ValidationResult(bool IsValid, string Message);

public static class JsonTools
{
    public static ValidationResult Validate(string json)
    {
        try
        {
            JsonDocument.Parse(json);
            return new ValidationResult(true, "JSON is valid.");
        }
        catch (JsonException ex)
        {
            var message =
                $"Invalid JSON at line {ex.LineNumber}, position {ex.BytePositionInLine}: {ex.Message}";
            return new ValidationResult(false, message);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"Validation failed: {ex.Message}");
        }
    }

    public static string Format(string json)
    {
        var parsed = JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON is empty.");
        return parsed.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public static string Minify(string json)
    {
        var parsed = JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON is empty.");
        return parsed.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        });
    }
}
