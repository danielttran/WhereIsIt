using System.Text.Json.Serialization;

namespace WhereIsIt.Pipe.Client;

public sealed record PipeRequest(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("op")] string Operation,
    [property: JsonPropertyName("corr")] string CorrelationId,
    [property: JsonPropertyName("query")] string? Query = null,
    [property: JsonPropertyName("sort_key")] string? SortKey = null,
    [property: JsonPropertyName("desc")] bool? Descending = null);

public sealed record PipeResponse(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("corr")] string CorrelationId,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error = null);
