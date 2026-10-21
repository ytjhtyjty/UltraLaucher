using System.Text;
using System.Text.RegularExpressions;

namespace UltraLauncher.Core.Minecraft;

/// <summary>
/// Minecraft с конфигурацией логирования Mojang пишет в stdout события log4j в XML.
/// Парсер собирает их в обычные строки «[время] [поток/УРОВЕНЬ] логгер: сообщение».
/// Строки не в XML-формате (старые версии, вывод JVM) проходят как есть.
/// </summary>
public sealed partial class Log4jEventParser
{
    private readonly StringBuilder _buffer = new();
    private bool _inEvent;

    /// <summary>Возвращает 0..n готовых строк для очередной строки вывода.</summary>
    public IEnumerable<string> Feed(string line)
    {
        if (!_inEvent)
        {
            if (line.TrimStart().StartsWith("<log4j:Event", StringComparison.Ordinal))
            {
                _inEvent = true;
                _buffer.Clear();
                _buffer.AppendLine(line);
                if (line.Contains("</log4j:Event>", StringComparison.Ordinal)) return Flush();
                return [];
            }
            return [line];
        }

        _buffer.AppendLine(line);
        return line.Contains("</log4j:Event>", StringComparison.Ordinal) ? Flush() : [];
    }

    private IEnumerable<string> Flush()
    {
        _inEvent = false;
        var xml = _buffer.ToString();
        _buffer.Clear();

        var head = EventHead().Match(xml);
        var logger = head.Success ? Attr(head.Value, "logger") : null;
        var level = head.Success ? Attr(head.Value, "level") : null;
        var thread = head.Success ? Attr(head.Value, "thread") : null;
        var ts = head.Success ? Attr(head.Value, "timestamp") : null;
        var message = Cdata(MessageBlock().Match(xml));
        var throwable = Cdata(ThrowableBlock().Match(xml));

        var time = long.TryParse(ts, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss") : "--:--:--";
        var shortLogger = logger is null ? "" : logger[(logger.LastIndexOf('.') + 1)..];
        var result = new List<string> { $"[{time}] [{thread}/{level}] {shortLogger}: {message}" };
        if (!string.IsNullOrEmpty(throwable))
            result.AddRange(throwable.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0));
        return result;
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, name + "=\"([^\"]*)\"");
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static string Cdata(Match m) => m.Success ? m.Groups[1].Value.Trim() : "";

    [GeneratedRegex(@"<log4j:Event[^>]*>", RegexOptions.Singleline)]
    private static partial Regex EventHead();

    [GeneratedRegex(@"<log4j:Message><!\[CDATA\[(.*?)\]\]></log4j:Message>", RegexOptions.Singleline)]
    private static partial Regex MessageBlock();

    [GeneratedRegex(@"<log4j:Throwable><!\[CDATA\[(.*?)\]\]></log4j:Throwable>", RegexOptions.Singleline)]
    private static partial Regex ThrowableBlock();
}
