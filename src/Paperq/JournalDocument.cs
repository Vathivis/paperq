using System.Globalization;
using System.Text;

namespace Paperq;

internal sealed record ResolutionSource(string Id, string Marker);

internal sealed record JournalPart(string Text, IReadOnlyList<ResolutionSource> Sources, string? Summary = null);

internal sealed class JournalDocument
{
    private const string GroupMarker = "<!-- paperq:consolidated -->";
    private const string GroupEndMarker = "<!-- paperq:consolidated:end -->";
    private readonly List<JournalPart> _parts;

    private JournalDocument(List<JournalPart> parts) => _parts = parts;

    internal static JournalDocument Parse(string content)
    {
        var parts = new List<JournalPart>();
        if (content.Length == 0)
        {
            return new JournalDocument(parts);
        }

        var header = content.IndexOf(ResolutionJournal.HeaderMarker, StringComparison.Ordinal);
        if (header < 0 || content.IndexOf("<!-- paperq:", StringComparison.Ordinal) != header)
        {
            throw Invalid();
        }

        var position = NextMarker(content, header + ResolutionJournal.HeaderMarker.Length);
        parts.Add(new JournalPart(content[..position], []));
        var markers = new HashSet<string>(StringComparer.Ordinal);
        while (position < content.Length)
        {
            if (content.AsSpan(position).StartsWith(GroupMarker, StringComparison.Ordinal))
            {
                var end = content.IndexOf(GroupEndMarker, position + GroupMarker.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw Invalid();
                }

                end += GroupEndMarker.Length;
                var text = content[position..end];
                var normalized = InputRules.NormalizeLineEndings(text);
                var lines = normalized.Split('\n');
                var sources = new List<ResolutionSource>();
                var line = 1;
                while (line < lines.Length && lines[line].StartsWith(ResolutionJournal.EntryMarkerPrefix, StringComparison.Ordinal))
                {
                    sources.Add(ParseSource(lines[line++]));
                }

                var delimiter = "\n### Resolution\n\n";
                var noteStart = normalized.IndexOf(delimiter, StringComparison.Ordinal);
                if (sources.Count == 0 || noteStart < 0)
                {
                    throw Invalid();
                }

                var summary = normalized[(noteStart + delimiter.Length)..^GroupEndMarker.Length].TrimEnd('\n');
                summary = InputRules.Validate(summary, "consolidated note");
                if (FormatGroup(sources, summary).TrimEnd('\n') != normalized)
                {
                    throw Invalid();
                }

                var next = NextMarker(content, end);
                // Include only the following separator in the managed group. Any
                // hand-written text after its explicit end marker stays separate.
                while (end < next && char.IsWhiteSpace(content[end]))
                {
                    end++;
                }

                parts.Add(new JournalPart(content[position..end], sources, summary));
                position = end;
                parts.Add(new JournalPart(content[position..next], []));
                position = next;
            }
            else
            {
                var next = NextMarker(content, position + 1);
                var text = content[position..next];
                var source = ValidateLegacyEntry(text);
                parts.Add(new JournalPart(text, [source]));
                position = next;
            }
        }

        foreach (var source in parts.SelectMany(part => part.Sources))
        {
            if (!markers.Add(source.Marker))
            {
                throw Invalid();
            }
        }

        return new JournalDocument(parts);
    }

    internal string Consolidate(IReadOnlySet<string> ids, string summary)
    {
        var selected = _parts.Where(part => part.Sources.Any(source => ids.Contains(source.Id))).ToArray();
        var sources = selected.SelectMany(part => part.Sources).ToArray();
        if (ids.Any(id => !sources.Any(source => source.Id == id)))
        {
            throw new PaperqException("resolution_not_found", "Each selected ID must have a journal entry.", PaperqExitCode.NotFound);
        }

        if (sources.Any(source => !ids.Contains(source.Id)))
        {
            throw new PaperqException("partial_consolidation", "Select every source ID in an existing consolidated group.", PaperqExitCode.Conflict);
        }

        if (selected.Length == 1 && selected[0].Summary == summary)
        {
            return string.Concat(_parts.Select(part => part.Text));
        }

        var result = new StringBuilder();
        var inserted = false;
        foreach (var part in _parts)
        {
            if (part.Sources.Any(source => ids.Contains(source.Id)))
            {
                if (!inserted)
                {
                    result.Append(FormatGroup(sources, summary));
                    inserted = true;
                }
            }
            else
            {
                result.Append(part.Text);
            }
        }

        return result.ToString();
    }

    internal (string Remaining, string Removed) Prune(IReadOnlySet<string> ids)
    {
        var remaining = new StringBuilder();
        var removed = new StringBuilder(ResolutionJournal.Header + "\n\n");
        var retainedSources = 0;
        foreach (var part in _parts)
        {
            var selected = part.Sources.Where(source => ids.Contains(source.Id)).ToArray();
            if (selected.Length == 0)
            {
                remaining.Append(part.Text);
                retainedSources += part.Sources.Count;
                continue;
            }

            var keep = part.Sources.Where(source => !ids.Contains(source.Id)).ToArray();
            if (part.Summary is not null)
            {
                removed.Append(FormatGroup(selected, part.Summary));
                if (keep.Length > 0)
                {
                    remaining.Append(FormatGroup(keep, part.Summary));
                }
            }
            else
            {
                removed.Append(part.Text);
            }

            retainedSources += keep.Length;
        }

        var result = remaining.ToString();
        if (retainedSources == 0 &&
            InputRules.NormalizeLineEndings(result).Trim() == ResolutionJournal.Header.Trim())
        {
            result = string.Empty;
        }

        return (result, removed.ToString());
    }

    private static string FormatGroup(IReadOnlyList<ResolutionSource> sources, string summary)
    {
        var markers = string.Join('\n', sources.Select(source => source.Marker));
        var links = string.Join('\n', sources.Select(source => source.Id).Distinct(StringComparer.Ordinal)
            .Select(id => $"- [{id}](.papercuts/resolved/{id}.md)"));
        return $"{GroupMarker}\n{markers}\n## Consolidated resolution\n\nSources:\n{links}\n\n### Resolution\n\n{summary}\n{GroupEndMarker}\n\n";
    }

    private static ResolutionSource ValidateLegacyEntry(string raw)
    {
        var text = InputRules.NormalizeLineEndings(raw).TrimEnd('\n');
        var markerEnd = text.IndexOf('\n');
        if (markerEnd < 0)
        {
            throw Invalid();
        }

        var source = ParseSource(text[..markerEnd]);
        var prefix = $"{source.Marker}\n## {source.Id}\n\nRecorded: ";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var dateEnd = text.IndexOf('\n', prefix.Length);
        if (dateEnd < 0 || !DateTimeOffset.TryParseExact(text[prefix.Length..dateEnd], "O",
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            throw Invalid();
        }

        var path = $".papercuts/resolved/{source.Id}.md";
        var problemPrefix = $"\nPapercut: [{path}]({path})\n\n### Problem\n\n";
        if (!text.AsSpan(dateEnd).StartsWith(problemPrefix, StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var start = dateEnd + problemPrefix.Length;
        const string delimiter = "\n\n### Resolution\n\n";
        for (var split = text.IndexOf(delimiter, start, StringComparison.Ordinal);
             split >= 0;
             split = text.IndexOf(delimiter, split + delimiter.Length, StringComparison.Ordinal))
        {
            var note = text[(split + delimiter.Length)..];
            if (ResolutionJournal.EntryMarker(source.Id, note) == source.Marker)
            {
                InputRules.Validate(text[start..split], "journal problem");
                InputRules.Validate(note, "journal note");
                return source;
            }
        }

        // A suffix that is not part of the hashed note may be hand-written text.
        // Refuse the rewrite rather than silently deleting that ambiguous content.
        throw Invalid();
    }

    private static ResolutionSource ParseSource(string marker)
    {
        if (!marker.StartsWith(ResolutionJournal.EntryMarkerPrefix, StringComparison.Ordinal) ||
            !marker.EndsWith(" -->", StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var key = marker[ResolutionJournal.EntryMarkerPrefix.Length..^4];
        var split = key.IndexOf(':');
        if (split < 0 || !PapercutId.IsValid(key[..split]) || key[(split + 1)..].Length != 16 ||
            key[(split + 1)..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw Invalid();
        }

        return new ResolutionSource(key[..split], marker);
    }

    private static int NextMarker(string content, int position)
    {
        var index = content.IndexOf("<!-- paperq:", position, StringComparison.Ordinal);
        if (index < 0)
        {
            return content.Length;
        }

        if (index > 0 && content[index - 1] != '\n')
        {
            throw Invalid();
        }

        return index;
    }

    private static PaperqException Invalid() => new(
        "invalid_resolution_journal",
        "The resolution journal contains malformed or ambiguous entries; no changes were made.",
        PaperqExitCode.InvalidData);
}
