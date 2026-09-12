using System.Text.RegularExpressions;

namespace ClashTray.Core;

public static partial class ErrorSanitizer
{
    private const int MaxErrorCharacters = 1024;

    public static string Sanitize(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Sanitize(exception.Message);
    }

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "发生未知错误。";
        }

        string sanitized = AuthorizationHeaderRegex().Replace(
            message.Trim(),
            match => $"{match.Groups["name"].Value}: [已隐藏]");
        sanitized = UrlRegex().Replace(sanitized, RedactUrl);
        sanitized = QuerySecretRegex().Replace(sanitized, RedactQuerySecret);
        sanitized = UserPathRegex().Replace(sanitized, "%USERPROFILE%");
        if (sanitized.Length <= MaxErrorCharacters)
        {
            return sanitized;
        }

        return sanitized[..(MaxErrorCharacters - 1)] + "…";
    }

    public static string? SanitizeNullable(string? message) =>
        string.IsNullOrWhiteSpace(message) ? message : Sanitize(message);

    private static string RedactUrl(Match match)
    {
        string value = match.Value;
        int queryIndex = value.IndexOf('?', StringComparison.Ordinal);
        int fragmentIndex = value.IndexOf('#', StringComparison.Ordinal);
        int sensitiveIndex = queryIndex >= 0 ? queryIndex : fragmentIndex;
        if (sensitiveIndex < 0)
        {
            return value;
        }

        int punctuationStart = value.Length;
        while (punctuationStart > sensitiveIndex && IsSentencePunctuation(value[punctuationStart - 1]))
        {
            punctuationStart--;
        }

        string punctuation = value[punctuationStart..];
        string marker = queryIndex >= 0 ? "?[已隐藏]" : "#[已隐藏]";
        return value[..sensitiveIndex] + marker + punctuation;
    }

    private static string RedactQuerySecret(Match match)
    {
        string value = match.Value;
        int separator = value.IndexOf('=', StringComparison.Ordinal);
        return separator < 0
            ? "[已隐藏]"
            : value[..(separator + 1)] + "[已隐藏]";
    }

    private static bool IsSentencePunctuation(char value) =>
        value is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}'
            or '。' or '，' or '；' or '：' or '！' or '？' or '）' or '】' or '》';

    [GeneratedRegex(
        @"(?i)\b(?<name>authorization|proxy-authorization)\s*:\s*(?:(?:bearer|basic|token)\s+)?[^\s,;，；]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(@"(?i)\bhttps?://[^\s""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(
        @"(?i)\b(?<name>token|access[_-]?token|auth|authorization|password|passwd|secret|api[_-]?key)\s*=\s*[^\s&#,;，；]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(
        @"(?i)\b[A-Z]:\\Users\\[^\\/:*?""<>|\r\n]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex UserPathRegex();
}
