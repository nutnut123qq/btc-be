using System.Text;

namespace Backend.Services;

/// <summary>
/// Splits text into overlapping chunks respecting paragraph and sentence boundaries.
/// </summary>
public static class TextChunker
{
    public static IReadOnlyList<string> Chunk(string text, int maxChars = 1000, int overlap = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        text = text.Trim();
        if (text.Length <= maxChars)
            return new[] { text };

        // 1. Split by paragraphs first
        var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var para in paragraphs)
        {
            if (current.Length + para.Length + 2 <= maxChars)
            {
                if (current.Length > 0) current.AppendLine();
                current.Append(para);
            }
            else
            {
                // Flush current chunk if non-empty
                if (current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }

                // If paragraph itself fits, start new chunk with it
                if (para.Length <= maxChars)
                {
                    current.Append(para);
                }
                else
                {
                    // Paragraph too long: split by sentences
                    var sentenceChunks = ChunkBySentences(para, maxChars, overlap);
                    chunks.AddRange(sentenceChunks);
                }
            }
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().Trim());

        // Apply overlap: each chunk (except first) starts with overlap text from previous chunk
        if (overlap > 0 && chunks.Count > 1)
        {
            var overlapped = new List<string>();
            for (int i = 0; i < chunks.Count; i++)
            {
                if (i == 0)
                {
                    overlapped.Add(chunks[i]);
                }
                else
                {
                    var prev = chunks[i - 1];
                    var prefix = prev.Length <= overlap ? prev : prev.Substring(prev.Length - overlap);
                    overlapped.Add(prefix + " " + chunks[i]);
                }
            }
            return overlapped;
        }

        return chunks;
    }

    private static IReadOnlyList<string> ChunkBySentences(string text, int maxChars, int overlap)
    {
        // Simple sentence splitting by . ! ? followed by space or end
        var sentences = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            sb.Append(text[i]);
            if ((text[i] == '.' || text[i] == '!' || text[i] == '?') && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                sentences.Add(sb.ToString().Trim());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            sentences.Add(sb.ToString().Trim());

        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var sent in sentences)
        {
            if (current.Length + sent.Length + 1 <= maxChars)
            {
                if (current.Length > 0) current.Append(' ');
                current.Append(sent);
            }
            else
            {
                if (current.Length > 0)
                    chunks.Add(current.ToString().Trim());
                current.Clear();
                current.Append(sent);
            }
        }
        if (current.Length > 0)
            chunks.Add(current.ToString().Trim());

        return chunks;
    }
}
