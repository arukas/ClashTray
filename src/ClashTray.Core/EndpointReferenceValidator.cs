using System.Text.RegularExpressions;

namespace ClashTray.Core;

internal static partial class EndpointReferenceValidator
{
    private const int MaxReferenceCharacters = 128;

    public static string Normalize(string reference, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference, parameterName);
        string normalized = reference.Trim();
        if (normalized.Length > MaxReferenceCharacters || !ReferenceRegex().IsMatch(normalized))
        {
            throw new ArgumentException("Endpoint reference contains unsupported characters or is too long.", parameterName);
        }

        return normalized;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();
}
