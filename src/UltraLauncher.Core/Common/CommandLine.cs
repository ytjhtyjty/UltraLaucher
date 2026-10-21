using System.Text;

namespace UltraLauncher.Core.Common;

public static class CommandLine
{
    /// <summary>Разбивает строку аргументов с учётом кавычек: <c>-Da="x y" -Xss2M</c>.</summary>
    public static List<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (hasToken) result.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(ch);
                hasToken = true;
            }
        }
        if (hasToken) result.Add(current.ToString());
        return result;
    }

    /// <summary>Строка для журнала: аргументы с пробелами в кавычках.</summary>
    public static string Join(IEnumerable<string> args)
        => string.Join(' ', args.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a));
}
