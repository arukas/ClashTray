namespace ClashTray.App;

internal static class SubscriptionImportInput
{
    public static bool TryParseUri(string? text, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text)
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? parsed)
            || parsed is null
            || string.IsNullOrWhiteSpace(parsed.Host))
        {
            return false;
        }

        if (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
