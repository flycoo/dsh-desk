using System.Text.RegularExpressions;

namespace DshDesk.Services;

public static partial class DshUrlParser
{
    [GeneratedRegex(@"dsh\s+web:\s*(?<url>https?://127\.0\.0\.1:\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReadyLineRegex();

    public static bool TryParseReadyLine(string? line, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = ReadyLineRegex().Match(line);
        return match.Success && Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out uri);
    }

    public static bool IsAllowedNavigation(Uri target, Uri dshOrigin)
    {
        if (target.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
        {
            return target.OriginalString.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
        }

        return target.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               target.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
               target.Host.Equals(dshOrigin.Host, StringComparison.OrdinalIgnoreCase) &&
               target.Port == dshOrigin.Port;
    }
}
