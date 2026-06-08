using System.Text.Json.Serialization;

namespace WhereIsIt.Pipe.Client;

/// <summary>
/// Newline-delimited JSON RPC exchanged between <see cref="NamedPipeEngineClient"/>
/// and <see cref="EnginePipeServer"/>. One <see cref="PipeRequest"/> per line in,
/// one <see cref="PipeResponse"/> per line out.
/// </summary>
public sealed record PipeRequest(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("op")] string Operation,
    [property: JsonPropertyName("corr")] string CorrelationId,
    [property: JsonPropertyName("query")] string? Query = null,
    [property: JsonPropertyName("sort_key")] string? SortKey = null,
    [property: JsonPropertyName("desc")] bool? Descending = null,
    [property: JsonPropertyName("row_id")] uint? RowId = null);

public sealed record PipeResponse(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("corr")] string CorrelationId,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("ids")] uint[]? Ids = null,
    [property: JsonPropertyName("row")] PipeRowDto? Row = null);

/// <summary>Wire form of <c>ResultRowModel</c>; times are ISO-8601 (UTC), empty = unknown.</summary>
public sealed record PipeRowDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("parent")] string ParentPath,
    [property: JsonPropertyName("size")] ulong SizeBytes,
    [property: JsonPropertyName("modified")] string ModifiedUtc,
    [property: JsonPropertyName("attributes")] string Attributes,
    [property: JsonPropertyName("created")] string? CreatedUtc = null,
    [property: JsonPropertyName("accessed")] string? AccessedUtc = null);

public static class PipeProtocol
{
    public const int Version = 2;
    public const string DefaultPipeName = "WhereIsIt.Engine";
}
