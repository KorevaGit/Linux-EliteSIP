using Avalonia;
using Avalonia.Logging;

namespace EliteSIP.App.Tests;

/// <summary>
/// Ошибки привязок за время проверки.
/// </summary>
///
/// <remarks>
/// Часть разметки привязана не компилятором, а отражением (выбор устройства,
/// вложенный DataContext), и опечатка там видна только в журнале Avalonia:
/// пустое поле на экране, а не ошибка сборки. Проверка окна поэтому ловит
/// журнал и падает на первой же строке о привязке — так WPF-версия ловила
/// бы <c>BindingExpression path error</c>, если бы её кто-то ловил.
/// </remarks>
internal sealed class BindingErrors : ILogSink, IDisposable
{
    private readonly ILogSink? _previous;
    private readonly List<string> _errors = [];

    internal BindingErrors()
    {
        _previous = Logger.Sink;
        Logger.Sink = this;
    }

    internal IReadOnlyList<string> Errors
    {
        get
        {
            lock (_errors)
            {
                return [.. _errors];
            }
        }
    }

    public bool IsEnabled(LogEventLevel level, string area) =>
        area == LogArea.Binding && level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (!IsEnabled(level, area))
        {
            return;
        }

        var text = messageTemplate;
        foreach (var value in propertyValues)
        {
            var start = text.IndexOf('{', StringComparison.Ordinal);
            var end = start < 0 ? -1 : text.IndexOf('}', start);
            if (end < 0)
            {
                break;
            }

            text = string.Concat(text.AsSpan(0, start), value?.ToString(), text.AsSpan(end + 1));
        }

        lock (_errors)
        {
            _errors.Add($"{source?.GetType().Name}: {text}");
        }
    }

    public void Dispose() => Logger.Sink = _previous;
}
