using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Paperq;

internal static class JsonOutput
{
    internal const int SchemaVersion = 1;

    internal static string Success(string command, Action<Utf8JsonWriter> writeData) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteBoolean("ok", true);
            writer.WriteString("command", command);
            writer.WriteStartObject("data");
            writeData(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    internal static string Error(string code, string message, int exitCode) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteBoolean("ok", false);
            writer.WriteStartObject("error");
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteNumber("exitCode", exitCode);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    internal static void WriteRecord(Utf8JsonWriter writer, PapercutRecord record, QueueLayout layout)
    {
        writer.WriteString("id", record.Id);
        writer.WriteString("state", record.State.ToDirectoryName());
        writer.WriteString("created", record.CreatedUtc);
        writer.WriteString("message", record.Message);
        writer.WriteString("path", layout.RelativePath(record.State, record.Id));
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
