using System.Text.Json;
using System.Text.Json.Nodes;

namespace Samples.SampleUtilities;

/// <summary>
/// System.Text.Json polymorphic deserialization requires <c>$type</c> before other properties on each
/// content object. The agent stack's <c>Serialize(List&lt;ChatMessage&gt;)</c> can still emit payload keys first;
/// this helper rewrites only <c>contents[]</c> entries (write-time, before persisting) so new DB rows deserialize.
/// </summary>
public static class ChatHistoryJsonNormalizer
{
    private const string TypeProperty = "$type";
    private const string ContentsCamel = "contents";
    private const string ContentsPascal = "Contents";

    /// <summary>
    /// Returns JSON equivalent to <paramref name="json"/> with each <c>contents[]</c> object reordered so
    /// <c>$type</c> is first when present. Intended for output of <c>JsonSerializer.Serialize</c> only.
    /// </summary>
    public static string EnsurePolymorphicDiscriminatorFirst(string json, JsonSerializerOptions options)
    {
        JsonNode? root = JsonNode.Parse(json);
        if (root is not JsonArray messages)
        {
            return json;
        }

        foreach (JsonNode? messageNode in messages)
        {
            if (messageNode is not JsonObject message)
            {
                continue;
            }

            JsonArray? contents = GetContentsArray(message);
            if (contents is null)
            {
                continue;
            }

            for (int i = 0; i < contents.Count; i++)
            {
                if (contents[i] is not JsonObject contentObj)
                {
                    continue;
                }

                if (!contentObj.TryGetPropertyValue(TypeProperty, out JsonNode? typeNode) || typeNode is null)
                {
                    continue;
                }

                var reordered = new JsonObject
                {
                    [TypeProperty] = typeNode.DeepClone(),
                };
                foreach (KeyValuePair<string, JsonNode?> pair in contentObj)
                {
                    if (pair.Key == TypeProperty)
                    {
                        continue;
                    }

                    reordered[pair.Key] = pair.Value?.DeepClone();
                }

                contents[i] = reordered;
            }
        }

        return messages.ToJsonString(options);
    }

    private static JsonArray? GetContentsArray(JsonObject message)
    {
        if (message.TryGetPropertyValue(ContentsCamel, out JsonNode? camel) && camel is JsonArray a1)
        {
            return a1;
        }

        if (message.TryGetPropertyValue(ContentsPascal, out JsonNode? pascal) && pascal is JsonArray a2)
        {
            return a2;
        }

        return null;
    }
}
