using System.Text.RegularExpressions;

namespace UltraLauncher.Core.Logging;

/// <summary>
/// Убирает токены и другие секреты из любого текста, который попадает в журнал или на экран.
/// Известные секреты регистрируются явно; дополнительно вырезаются JWT и значения после --accessToken.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "***";
    private static readonly HashSet<string> Secrets = new(StringComparer.Ordinal);
    private static readonly object Lock = new();

    public static void Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 8) return;
        lock (Lock) Secrets.Add(secret);
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        string[] known;
        lock (Lock) known = [.. Secrets];
        foreach (var s in known)
            text = text.Replace(s, Mask, StringComparison.Ordinal);

        text = AccessTokenArg().Replace(text, "$1" + Mask);
        text = Jwt().Replace(text, Mask);
        text = BearerHeader().Replace(text, "$1" + Mask);
        text = XblHeader().Replace(text, "XBL3.0 x=" + Mask);
        return text;
    }

    [GeneratedRegex(@"(--accessToken[\s=]+)(?!\*\*\*)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex AccessTokenArg();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(Bearer\s+)(?!\*\*\*)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerHeader();

    [GeneratedRegex(@"XBL3\.0 x=\S+")]
    private static partial Regex XblHeader();
}
