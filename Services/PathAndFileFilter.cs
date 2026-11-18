using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FreeAutocorrect.Models;

namespace FreeAutocorrect.Services;

/// <summary>
/// Comprehensive filter engine that detects and ignores:
/// 1. File types and extensions (.exe, .docx, .pdf, .png, .json, etc.)
/// 2. File names (e.g. report.docx, index.html, FreeAutocorrect.csproj, archive.tar.gz)
/// 3. Directory paths (e.g. C:\Users\..., c:/path/..., ./src/components/, /usr/bin/, \\share\)
/// 4. Technical / Code identifiers (CamelCase, snake_case, alphanumeric tokens, hex, flags)
/// </summary>
public static class PathAndFileFilter
{
    // Comprehensive set of known file types / extensions (case-insensitive)
    public static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Programming & Web
        "cs", "rs", "py", "js", "ts", "jsx", "tsx", "html", "htm", "css", "scss", "sass", "less",
        "json", "jsonc", "xml", "yaml", "yml", "toml", "sql", "php", "rb", "go", "java", "kt", "kts",
        "c", "cpp", "cc", "cxx", "h", "hpp", "hxx", "swift", "m", "mm", "dart", "sh", "bash", "zsh",
        "ps1", "psm1", "bat", "cmd", "lua", "r", "graphql", "gql", "proto", "vue", "svelte", "wasm",
        "asm", "s", "pl", "pm", "tcl", "vb", "vbs", "fs", "fsi", "fsx", "clj", "cljs", "scala",

        // Documents & Text
        "txt", "md", "markdown", "rst", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx",
        "csv", "tsv", "rtf", "tex", "odt", "ods", "odp", "epub", "pages", "numbers", "key",

        // Binaries, Archives & Packages
        "exe", "dll", "bin", "so", "dylib", "msi", "iso", "img", "zip", "tar", "gz", "tgz", "bz2",
        "7z", "rar", "xz", "apk", "aab", "jar", "war", "ear", "deb", "rpm", "pkg", "dmg", "tar.gz",
        "min.js", "min.css", "xaml.cs",

        // Project, Build & Config Files
        "csproj", "sln", "vbproj", "fsproj", "vcxproj", "pbxproj", "gradle", "properties",
        "env", "config", "conf", "ini", "cfg", "lock", "log", "bak", "tmp", "temp", "pub", "pem", "crt",
        "editorconfig", "gitignore", "gitattributes", "dockerfile", "makefile", "xaml", "axml", "bicep", "tf",

        // Images & Graphics
        "png", "jpg", "jpeg", "gif", "bmp", "webp", "svg", "ico", "tiff", "tif", "psd", "ai",
        "eps", "raw", "cr2", "nef", "heic", "avif",

        // Audio & Video
        "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus", "aiff",
        "mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg", "3gp",

        // Fonts
        "ttf", "otf", "woff", "woff2", "eot"
    };

    // Common standard abbreviations that should never trigger sentence capitalization
    public static readonly HashSet<string> CommonAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "eg", "ie", "etc", "vs", "mr", "mrs", "ms", "dr", "prof", "sr", "jr",
        "inc", "ltd", "corp", "co", "fig", "al", "dept", "vol", "no", "approx"
    };

    // Regex for Windows, Unix, Relative, and UNC Directory / File Paths
    private static readonly Regex PathRegex = new(
        @"(?i)(?:" +
        @"[a-z]:[\\/][^\s<>()""']+|" +                                 // Windows drive path: C:\... or C:/...
        @"\\\\[a-z0-9_.\-]+[\\/][^\s<>()""']+|" +                     // Windows UNC path: \\server\share\...
        @"(?:\.\.[\\/]+|\.[\\/]+)[a-z0-9_.\-/\\*]+|" +                // Relative path: .\... or ..\.. or ./...
        @"~[\\/][^\s<>()""']+|" +                                     // Tilde home path: ~/folder...
        @"/(?:usr|etc|var|home|bin|dev|lib|opt|tmp|root|mnt|sys|proc|Users)[^\s<>()""']*|" + // Unix root paths
        @"\b[a-z0-9_.\-]+[\\/][a-z0-9_.\-/\\*]+\b" +                  // Any word with slash or backslash: folder/file, dir\subdir
        @")",
        RegexOptions.Compiled
    );

    // Regex for file names with extensions (e.g. report.docx, index.html, archive.tar.gz, .gitignore, docker-compose.yml)
    private static readonly Regex FileNameRegex = new(
        @"(?i)(?:" +
        @"\b[a-z0-9_+\-~]+(?:\.[a-z0-9_+\-~]+)*\.[a-z0-9]{1,12}\b|" + // Standard filename: stem.ext or stem.min.js
        @"\.[a-z0-9_\-]{2,12}\b|" +                                    // Bare dot extension or hidden file: .gitignore, .docx
        @"\*[a-z0-9_.\-]*" +                                           // Wildcards: *.txt, *.*
        @")",
        RegexOptions.Compiled
    );

    // Regex for Markdown fenced code blocks: ```...``` or ~~~...~~~
    private static readonly Regex FencedCodeBlockRegex = new(
        @"```[\s\S]*?```|~~~[\s\S]*?~~~",
        RegexOptions.Compiled
    );

    // Regex for Markdown inline code spans: `...`
    private static readonly Regex InlineCodeRegex = new(
        @"`[^`\r\n]+`",
        RegexOptions.Compiled
    );

    // Regex for SemVer versions, e.g. v1.2.3, 2.0.0-rc.1, 0.0.26
    private static readonly Regex SemVerRegex = new(
        @"\bv?[0-9]+\.[0-9]+(?:\.[0-9]+)*(?:-[a-z0-9_.\-]+)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // Regex for Windows/Unix Environment Variables: %APPDATA%, $PATH, ${HOME}
    private static readonly Regex EnvVarRegex = new(
        @"%[a-zA-Z_0-9]+%|\$(?:[a-zA-Z_][a-zA-Z0-9_]*|\{[a-zA-Z_][a-zA-Z0-9_]*\})",
        RegexOptions.Compiled
    );

    /// <summary>
    /// Checks if a string or token represents a known file type / extension.
    /// Supports with or without dot (e.g. ".docx", "docx", ".PDF", "pdf").
    /// </summary>
    public static bool IsKnownFileType(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        string clean = token.Trim().TrimStart('.').TrimEnd(',', ';', ':', '!', '?');
        if (KnownExtensions.Contains(clean)) return true;

        // Check compound extensions (e.g. tar.gz, min.js, min.css)
        int lastDot = clean.LastIndexOf('.');
        if (lastDot > 0)
        {
            string ext = clean.Substring(lastDot + 1);
            if (KnownExtensions.Contains(ext)) return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a token is a valid file name (e.g. report.docx, FreeAutocorrect.csproj, README.md, .gitignore).
    /// </summary>
    public static bool IsFileName(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        string clean = token.Trim().TrimEnd(',', ';', ':', '!', '?');

        // Hidden files like .gitignore, .env, or extensions like .docx, .pdf
        if (clean.StartsWith(".") && clean.Length > 2 && !clean.Contains(" "))
        {
            string stem = clean.Substring(1);
            if (KnownExtensions.Contains(stem) ||
                stem.Equals("gitignore", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("gitattributes", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("env", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("editorconfig", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("eslintrc", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("npmrc", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("prettierrc", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("dockerignore", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Must contain a dot for filename extension
        int dotIndex = clean.LastIndexOf('.');
        if (dotIndex > 0 && dotIndex < clean.Length - 1)
        {
            string ext = clean.Substring(dotIndex + 1);
            if (KnownExtensions.Contains(ext)) return true;

            // Secondary extension check for compound extensions (e.g. .tar.gz)
            int secondDot = clean.Substring(0, dotIndex).LastIndexOf('.');
            if (secondDot > 0)
            {
                string compoundExt = clean.Substring(secondDot + 1);
                if (compoundExt.Equals("tar.gz", StringComparison.OrdinalIgnoreCase) ||
                    compoundExt.Equals("min.js", StringComparison.OrdinalIgnoreCase) ||
                    compoundExt.Equals("min.css", StringComparison.OrdinalIgnoreCase) ||
                    compoundExt.Equals("xaml.cs", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a token is a directory or file path.
    /// </summary>
    public static bool IsPath(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        string clean = token.Trim();

        // Contains path separators
        if (clean.Contains('\\') || clean.Contains('/'))
        {
            // Avoid flagging simple division like "1/2" or prose like "and/or"
            if (clean.Length >= 2)
            {
                if (clean.StartsWith("/") || clean.StartsWith("\\") ||
                    clean.StartsWith("./") || clean.StartsWith(".\\") ||
                    clean.StartsWith("../") || clean.StartsWith("..\\") ||
                    clean.StartsWith("~/") || clean.StartsWith("~\\"))
                {
                    return true;
                }

                // Windows drive letter: C:\ or c:/
                if (clean.Length >= 3 && char.IsLetter(clean[0]) && clean[1] == ':' && (clean[2] == '\\' || clean[2] == '/'))
                {
                    return true;
                }

                // Subdirectory pattern: word/word or word\word
                if (PathRegex.IsMatch(clean))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a token is a technical code symbol, identifier, or parameter that causes phantom errors:
    /// - CamelCase / PascalCase identifiers (e.g. GetSelectedText, FreeAutocorrect)
    /// - snake_case identifiers (e.g. user_id, active_window)
    /// - Alphanumeric tokens with mixed letters & digits (e.g. net9, win32, x64, v0.0.25)
    /// - Hex values (e.g. 0xFF, 0xB03F0)
    /// - CLI flags (e.g. --test, -v, /s)
    /// </summary>
    public static bool IsCodeOrTechnicalIdentifier(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        string clean = token.Trim().TrimEnd(',', ';', ':', '!', '?');
        if (clean.Length < 2) return false;

        // CLI flags
        if (clean.StartsWith("--") || (clean.StartsWith("-") && clean.Length > 1 && char.IsLetter(clean[1])))
        {
            return true;
        }

        // Hex numbers: 0x1F, 0xB03F0
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && clean.Length > 2)
        {
            return true;
        }

        // snake_case: contains underscore
        if (clean.Contains('_'))
        {
            return true;
        }

        // Alphanumeric mix with digits and letters (e.g. net9, win32, x64, sha256, v0.0.26)
        bool hasLetter = clean.Any(char.IsLetter);
        bool hasDigit = clean.Any(char.IsDigit);
        if (hasLetter && hasDigit)
        {
            return true;
        }

        // SemVer format (e.g. v1.2.3, 2.0.0-rc.1, 0.0.27)
        if (SemVerRegex.IsMatch(clean))
        {
            return true;
        }

        // Environment variables (e.g. %APPDATA%, $PATH)
        if ((clean.StartsWith("%") && clean.EndsWith("%") && clean.Length > 2) ||
            clean.StartsWith("$"))
        {
            return true;
        }

        // CamelCase / PascalCase: has a lowercase letter followed directly by an uppercase letter
        for (int i = 1; i < clean.Length; i++)
        {
            if (char.IsLower(clean[i - 1]) && char.IsUpper(clean[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Evaluates whether a token should be completely ignored for spell checking, autocorrect, and suggestions.
    /// </summary>
    public static bool ShouldIgnoreToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        if (IsKnownFileType(token)) return true;
        if (IsFileName(token)) return true;
        if (IsPath(token)) return true;
        if (IsCodeOrTechnicalIdentifier(token)) return true;

        return false;
    }

    /// <summary>
    /// Scans text and returns all character index spans [Start, End) that belong to:
    /// - Links, URLs, web domains, and emails
    /// - Directory paths and file paths
    /// - File names and file types
    /// </summary>
    public static List<(int Start, int End)> GetIgnoredSpans(string text)
    {
        var spans = new List<(int Start, int End)>();
        if (string.IsNullOrEmpty(text)) return spans;

        // 1. URLs, links, web domains, emails (from LinkFilterHelper)
        var linkRanges = LinkFilterHelper.GetLinkRanges(text);
        spans.AddRange(linkRanges);

        // 2. Directory and file paths
        var pathMatches = PathRegex.Matches(text);
        foreach (Match m in pathMatches)
        {
            if (m.Success && m.Length > 0)
            {
                int start = m.Index;
                int end = m.Index + m.Length;
                while (end > start && (text[end - 1] == '.' || text[end - 1] == ',' || text[end - 1] == ';' ||
                                       text[end - 1] == ':' || text[end - 1] == '!' || text[end - 1] == '?' ||
                                       text[end - 1] == ')' || text[end - 1] == '"' || text[end - 1] == '\''))
                {
                    end--;
                }
                if (end > start) spans.Add((start, end));
            }
        }

        // 3. File names with extensions
        var fileMatches = FileNameRegex.Matches(text);
        foreach (Match m in fileMatches)
        {
            if (m.Success && m.Length > 0)
            {
                string val = m.Value;
                if (IsFileName(val) || IsKnownFileType(val))
                {
                    int start = m.Index;
                    int end = m.Index + m.Length;
                    while (end > start && (text[end - 1] == '.' || text[end - 1] == ',' || text[end - 1] == ';' ||
                                           text[end - 1] == ':' || text[end - 1] == '!' || text[end - 1] == '?' ||
                                           text[end - 1] == ')' || text[end - 1] == '"' || text[end - 1] == '\''))
                    {
                        end--;
                    }
                    if (end > start) spans.Add((start, end));
                }
            }
        }

        // 4. Markdown fenced code blocks: ```...``` or ~~~...~~~
        var codeBlockMatches = FencedCodeBlockRegex.Matches(text);
        foreach (Match m in codeBlockMatches)
        {
            if (m.Success && m.Length > 0)
            {
                spans.Add((m.Index, m.Index + m.Length));
            }
        }

        // 5. Markdown inline code spans: `...`
        var inlineCodeMatches = InlineCodeRegex.Matches(text);
        foreach (Match m in inlineCodeMatches)
        {
            if (m.Success && m.Length > 0)
            {
                spans.Add((m.Index, m.Index + m.Length));
            }
        }

        // 6. SemVer versions & Environment variables
        var semVerMatches = SemVerRegex.Matches(text);
        foreach (Match m in semVerMatches)
        {
            if (m.Success && m.Length > 0)
            {
                spans.Add((m.Index, m.Index + m.Length));
            }
        }

        var envVarMatches = EnvVarRegex.Matches(text);
        foreach (Match m in envVarMatches)
        {
            if (m.Success && m.Length > 0)
            {
                spans.Add((m.Index, m.Index + m.Length));
            }
        }

        // Merge overlapping spans
        if (spans.Count <= 1) return spans;

        var sorted = spans.OrderBy(s => s.Start).ThenBy(s => s.End).ToList();
        var merged = new List<(int Start, int End)> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            var cur = sorted[i];
            var prev = merged[^1];

            if (cur.Start <= prev.End)
            {
                merged[^1] = (prev.Start, Math.Max(prev.End, cur.End));
            }
            else
            {
                merged.Add(cur);
            }
        }

        return merged;
    }

    /// <summary>
    /// Checks if a span in text falls inside or intersects any ignored span (URL, path, filename, file type).
    /// </summary>
    public static bool IsSpanIgnored(int index, int length, List<(int Start, int End)> ignoredSpans)
    {
        if (ignoredSpans == null || ignoredSpans.Count == 0 || length <= 0) return false;

        int wordEnd = index + length;
        foreach (var (start, end) in ignoredSpans)
        {
            if (index < end && wordEnd > start)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Filters a list of LintItems, removing any phantom errors caused by:
    /// - File types (.docx, .pdf, .exe, etc.)
    /// - File names (report.docx, index.html, etc.)
    /// - Directory paths (C:\..., /usr/bin, etc.)
    /// - Code identifiers (CamelCase, snake_case, alphanumeric tokens)
    /// - Spans falling inside paths, URLs, or filenames
    /// </summary>
    public static List<LintItem> FilterLints(List<LintItem> lints, string sourceText)
    {
        if (lints == null || lints.Count == 0) return new List<LintItem>();

        var ignoredSpans = GetIgnoredSpans(sourceText);
        var result = new List<LintItem>();

        foreach (var lint in lints)
        {
            if (string.IsNullOrEmpty(lint.MatchedText)) continue;
            string matched = lint.MatchedText.Trim();

            // 1. Direct token check (for non-whitespace tokens)
            if (matched.Length > 0 && ShouldIgnoreToken(matched)) continue;

            // 2. Span intersection check
            int len = Math.Max(1, lint.CharEnd - lint.CharStart);
            if (IsSpanIgnored(lint.CharStart, len, ignoredSpans)) continue;

            // 3. Sentence capitalization false positive check:
            // If rule is sentence capitalization but matched text was right after a dot with no whitespace (e.g. "file.txt")
            if (lint.Rule != null && lint.Rule.Contains("Capital", StringComparison.OrdinalIgnoreCase))
            {
                if (lint.CharStart > 0 && sourceText.Length >= lint.CharStart)
                {
                    int prevIdx = lint.CharStart - 1;
                    if (prevIdx >= 0 && sourceText[prevIdx] == '.')
                    {
                        // Word directly follows '.' with 0 whitespace (e.g. "file.txt" or "app.config") -> ignore
                        continue;
                    }
                }
            }

            result.Add(lint);
        }

        return result;
    }

    /// <summary>
    /// Live typing context check: inspects the buffer around current word to detect
    /// if the user is typing inside or completing a path, file name, file type, or link.
    /// </summary>
    public static bool IsPathOrFileOrLinkContext(string buffer, int wordLength, char delimiter)
    {
        if (string.IsNullOrEmpty(buffer)) return false;

        // 1. Check link or URL context
        if (LinkFilterHelper.IsLinkOrUrlContext(buffer, wordLength, delimiter))
        {
            return true;
        }

        // 2. Find contiguous non-whitespace token ending at buffer.Length
        int end = buffer.Length;
        int start = end - 1;
        while (start >= 0 && !char.IsWhiteSpace(buffer[start]))
        {
            start--;
        }
        start++;

        if (start >= end) return false;
        string token = buffer.Substring(start, end - start).Trim();
        if (string.IsNullOrEmpty(token)) return false;

        // 3. Path separators: backslash or slash
        if (token.Contains('\\') || token.Contains('/'))
        {
            return true;
        }

        // 4. Delimiter is path separator or extension dot
        if (delimiter == '\\' || delimiter == '/')
        {
            return true;
        }

        // 5. If delimiter is '.', check if token is a filename stem or path
        if (delimiter == '.' && (token.Contains('.') || token.Contains('\\') || token.Contains('/')))
        {
            return true;
        }

        // 6. Token is a known file type or filename
        if (IsKnownFileType(token) || IsFileName(token) || IsPath(token))
        {
            return true;
        }

        // 7. Token contains dot and followed by letters (e.g. "notes.tx" while typing "notes.txt")
        int dot = token.LastIndexOf('.');
        if (dot > 0 && dot < token.Length - 1)
        {
            string extCandidate = token.Substring(dot + 1);
            if (KnownExtensions.Any(k => k.StartsWith(extCandidate, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
