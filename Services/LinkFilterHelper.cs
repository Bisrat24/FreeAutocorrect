using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FreeAutocorrect.Services;

public static class LinkFilterHelper
{
    // Regex matching full URLs, web addresses, protocols, domains with popular TLDs, emails, and file paths
    private static readonly Regex UrlRegex = new(
        @"(?i)\b(?:https?|ftp|file)://[^\s<>()""']+|" +
        @"\bwww\.[a-z0-9\-]+(?:\.[a-z0-9\-]+)*(?::[0-9]+)?(?:/[^\s<>()""']*)?|" +
        @"\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b|" +
        @"\bgit@[a-z0-9.-]+:[^\s<>()""']+|" +
        @"\b(?:localhost|127\.0\.0\.1)(?::[0-9]+)?(?:/[^\s<>()""']*)?|" +
        @"\b[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.(?:com|org|net|edu|gov|io|ai|dev|app|co|uk|de|ca|me|info|biz|tv|xyz|tech|site|online|us|eu)(?::[0-9]+)?(?:/[^\s<>()""']*)?\b|" +
        @"\b[a-z]:\\[^\s<>()""']+",
        RegexOptions.Compiled
    );

    // Markdown link pattern: [link text](url) -> extracts URL range
    private static readonly Regex MarkdownLinkRegex = new(
        @"\[([^\]]*)\]\(([^)]+)\)",
        RegexOptions.Compiled
    );

    /// <summary>
    /// Returns all character index spans [Start, End) in text that belong to links, URLs, emails, or file paths.
    /// </summary>
    public static List<(int Start, int End)> GetLinkRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        if (string.IsNullOrEmpty(text)) return ranges;

        // 1. Direct URLs, web addresses, domains, emails, paths
        var matches = UrlRegex.Matches(text);
        foreach (Match m in matches)
        {
            if (m.Success && m.Length > 0)
            {
                int start = m.Index;
                int end = m.Index + m.Length;

                // Trim trailing punctuation that belongs to surrounding prose (e.g. "Visit https://google.com.")
                while (end > start && (text[end - 1] == '.' || text[end - 1] == ',' ||
                                       text[end - 1] == ';' || text[end - 1] == ':' ||
                                       text[end - 1] == '!' || text[end - 1] == '?' ||
                                       text[end - 1] == ')' || text[end - 1] == '>' ||
                                       text[end - 1] == ']' || text[end - 1] == '"' ||
                                       text[end - 1] == '\''))
                {
                    end--;
                }

                if (end > start)
                {
                    ranges.Add((start, end));
                }
            }
        }

        // 2. Markdown links: target URL in parentheses
        var mdMatches = MarkdownLinkRegex.Matches(text);
        foreach (Match m in mdMatches)
        {
            if (m.Success && m.Groups.Count > 2)
            {
                var urlGroup = m.Groups[2];
                if (urlGroup.Success && urlGroup.Length > 0)
                {
                    ranges.Add((urlGroup.Index, urlGroup.Index + urlGroup.Length));
                }
            }
        }

        return ranges;
    }

    /// <summary>
    /// Checks if a span [index, index + length) falls inside or intersects any link range.
    /// </summary>
    public static bool IsInsideLink(int index, int length, List<(int Start, int End)> linkRanges)
    {
        if (linkRanges == null || linkRanges.Count == 0 || length <= 0) return false;

        int wordEnd = index + length;
        foreach (var (start, end) in linkRanges)
        {
            // If the word overlaps with the link range
            if (index < end && wordEnd > start)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a span in text falls inside or intersects any link range.
    /// </summary>
    public static bool IsInsideLink(string text, int index, int length)
    {
        if (string.IsNullOrEmpty(text) || length <= 0) return false;
        var ranges = GetLinkRanges(text);
        return IsInsideLink(index, length, ranges);
    }

    /// <summary>
    /// Inspects the typing buffer context immediately before and around the current word
    /// to determine if the user is typing inside or completing a URL, web address, domain, or email.
    /// </summary>
    public static bool IsLinkOrUrlContext(string buffer, int wordLength, char delimiter)
    {
        if (string.IsNullOrEmpty(buffer)) return false;

        // Find the contiguous non-whitespace token ending at buffer.Length
        int end = buffer.Length;
        int start = end - 1;
        while (start >= 0 && !char.IsWhiteSpace(buffer[start]))
        {
            start--;
        }
        start++; // first non-whitespace char

        if (start >= end) return false;

        string token = buffer.Substring(start, end - start).Trim();
        if (string.IsNullOrEmpty(token)) return false;

        // 1. Token contains URL scheme or common link prefix
        if (token.Contains("://", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("ftp.", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
            token.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Token contains '@' (email or git SSH link)
        if (token.Contains('@'))
        {
            return true;
        }

        // 3. Delimiter is a URL-specific separator (':', '/', '?', '#', '&', '=', '_', '%')
        if (delimiter == ':' || delimiter == '/' || delimiter == '?' ||
            delimiter == '#' || delimiter == '&' || delimiter == '=' || delimiter == '%')
        {
            // If preceded by letters or protocol prefix, it's a URL component
            if (token.Contains('/') || token.Contains(':') || token.Contains('.') || token.Length >= 2)
            {
                return true;
            }
        }

        // 4. Token contains domain suffix followed by delimiter or slash
        if (token.Contains(".com", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".org", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".net", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".edu", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".gov", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".io", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".ai", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".dev", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".app", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".co", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".me", StringComparison.OrdinalIgnoreCase) ||
            token.Contains(".xyz", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 5. Delimiter is '.' and the token is 'www' or a common domain prefix
        if (delimiter == '.' && (token.Equals("www", StringComparison.OrdinalIgnoreCase) ||
                                 token.Equals("ftp", StringComparison.OrdinalIgnoreCase) ||
                                 token.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                                 token.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 6. Token matches UrlRegex
        if (UrlRegex.IsMatch(token))
        {
            return true;
        }

        return false;
    }
}
