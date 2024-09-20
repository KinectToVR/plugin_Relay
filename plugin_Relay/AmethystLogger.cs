using System;
using System.Collections.Concurrent;
using Amethyst.Plugins.Contract;
using Microsoft.Extensions.Logging;

namespace plugin_Relay;

public sealed class AmethystHostLogger(string name, IAmethystHost host) : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        return default!;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if (logLevel < LogLevel.Information || host is null) return;
        host.Log($"[{eventId.Id}: {logLevel}] {name} - {formatter(state, exception)}");
    }
}

[ProviderAlias("AmethystHost")]
public sealed class AmethystHostLoggerProvider(IAmethystHost host) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, AmethystHostLogger> _loggers =
        new(StringComparer.OrdinalIgnoreCase);

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new AmethystHostLogger(name, host));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }
}