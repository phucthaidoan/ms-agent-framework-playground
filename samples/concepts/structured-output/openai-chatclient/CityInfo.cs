using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Samples.StructuredOutput.OpenAIChatClient;

[Description("Information about a city")]
public sealed class CityInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }
}
