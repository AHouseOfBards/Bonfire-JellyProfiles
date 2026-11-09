using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

// Keeps every message the middleware logs, formatted as it would be printed, with its level.
public sealed class CapLogger<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        => CapLogger.Lines?.Add((logLevel.ToString(), formatter(state, exception)));
}

public static class CapLogger
{
    public static List<(string level, string text)> Lines;
}
