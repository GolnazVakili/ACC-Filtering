using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AccFiltering.Services;

/// <summary>Writes log lines through Spectre so they stay readable next to prompts.</summary>
public sealed class SpectreLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minimum;

    /// <summary>Creates a provider that prints <paramref name="minimum"/> and above.</summary>
    public SpectreLoggerProvider(LogLevel minimum) => _minimum = minimum;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new SpectreLogger(categoryName, _minimum);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class SpectreLogger : ILogger
    {
        private static readonly object Gate = new();
        private readonly string _category;
        private readonly LogLevel _minimum;

        public SpectreLogger(string categoryName, LogLevel minimum)
        {
            var separator = categoryName.LastIndexOf('.');
            _category = separator >= 0 ? categoryName[(separator + 1)..] : categoryName;
            _minimum = minimum;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimum && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null && !message.Contains(exception.Message, StringComparison.Ordinal))
            {
                message = message + " " + exception.Message;
            }

            var color = logLevel switch
            {
                LogLevel.Error or LogLevel.Critical => "red",
                LogLevel.Warning => "yellow",
                LogLevel.Debug or LogLevel.Trace => "grey",
                _ => "silver"
            };

            var line = $"{logLevel}: {_category}: {message}";
            lock (Gate)
            {
                AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(line)}[/]");
            }
        }
    }
}
