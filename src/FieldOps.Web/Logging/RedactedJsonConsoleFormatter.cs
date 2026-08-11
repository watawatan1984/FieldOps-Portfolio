using System.Buffers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace FieldOps.Web.Logging;

public sealed class RedactedJsonConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "fieldops-json";

    private static readonly HashSet<string> AllowedEventProperties =
    [
        "Operation",
        "Outcome",
        "CorrelationId",
        "UserId",
        "Role",
        "Route",
        "StatusCode",
        "ElapsedMs",
        "MutationElapsedMs",
        "LockWaitElapsedMs",
        "SaveChangesElapsedMs",
        "CommitElapsedMs",
        "ExceptionCategory",
        "ExceptionType"
    ];

    private static readonly HashSet<string> AllowedScopeProperties =
    [
        "CorrelationId",
        "UserId",
        "Role",
        "Route"
    ];

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        if (!logEntry.Category.StartsWith("FieldOps.", StringComparison.Ordinal))
        {
            return;
        }

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("TimestampUtc", DateTimeOffset.UtcNow);
            writer.WriteString("LogLevel", logEntry.LogLevel.ToString());
            writer.WriteString("Category", logEntry.Category);
            writer.WriteNumber("EventId", logEntry.EventId.Id);
            writer.WriteString("Message", logEntry.Formatter(logEntry.State, null));
            WriteProperties(
                writer,
                logEntry.State is IEnumerable<KeyValuePair<string, object?>> eventProperties ? eventProperties : null,
                AllowedEventProperties);

            writer.WriteStartObject("Scopes");
            scopeProvider?.ForEachScope((scope, jsonWriter) =>
            {
                WriteProperties(
                    jsonWriter,
                    scope as IEnumerable<KeyValuePair<string, object?>>,
                    AllowedScopeProperties);
            }, writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static void WriteProperties(
        Utf8JsonWriter writer,
        IEnumerable<KeyValuePair<string, object?>>? properties,
        IReadOnlySet<string> allowedNames)
    {
        if (properties is null)
        {
            return;
        }

        foreach (KeyValuePair<string, object?> property in properties)
        {
            if (!allowedNames.Contains(property.Key))
            {
                continue;
            }

            writer.WritePropertyName(property.Key);
            JsonSerializer.Serialize(writer, property.Value, property.Value?.GetType() ?? typeof(object));
        }
    }
}