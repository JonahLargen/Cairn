using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cairn.AspNetCore.Tests;

// Support for asserting on Cairn's emit-miss diagnostics (CairnLinkRecorder.RegisterEmitMissDiagnostic).
// Those diagnostics — the "never emitted" and "custom JsonConverter" warnings and the unemitted meter — are
// written from a Response.OnCompleted callback, which the TestServer runs on a continuation that can land
// *after* the client's GetStringAsync/SendAsync has already returned the body. A test therefore cannot read
// the log the instant the request returns; it must wait for the callback. These helpers make that wait
// event-driven and deterministic instead of a sleep or a poll.

/// <summary>
/// Captures every formatted log message and lets a test await a specific one. Use for a *positive* assertion
/// ("the diagnostic was logged") — <see cref="WaitForAsync"/> completes the moment a matching message arrives.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];
    private readonly List<(Func<string, bool> Predicate, TaskCompletionSource Signal)> _waiters = [];

    /// <summary>A snapshot of everything logged so far; safe to enumerate while the host keeps logging.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    /// <summary>
    /// Completes as soon as a logged message matches <paramref name="predicate"/> — event-driven, no polling.
    /// On timeout it returns rather than throws, so the caller's own assertion produces the real,
    /// message-rich failure instead of a bare <see cref="TimeoutException"/>.
    /// </summary>
    public async Task WaitForAsync(Func<string, bool> predicate, TimeSpan? timeout = null)
    {
        Task signal;
        lock (_gate)
        {
            if (_messages.Any(predicate))
            {
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((predicate, tcs));
            signal = tcs.Task;
        }

        try
        {
            await signal.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
        }
    }

    private void Record(string message)
    {
        List<TaskCompletionSource>? ready = null;
        lock (_gate)
        {
            _messages.Add(message);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Predicate(message))
                {
                    (ready ??= []).Add(_waiters[i].Signal);
                    _waiters.RemoveAt(i);
                }
            }
        }

        // Complete the signals outside the lock so continuations never run under it.
        if (ready is not null)
        {
            foreach (var signal in ready)
            {
                signal.TrySetResult();
            }
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Record(formatter(state, exception));
    }
}

/// <summary>
/// A deterministic "this response has fully finished" signal, for a *negative* assertion ("the diagnostic was
/// NOT logged") — you cannot wait for an event that never fires, so instead wait until the emit-miss callback
/// has provably run, then assert its absence. Registered as the outermost middleware, this signal's
/// Response.OnCompleted callback is the first one registered and therefore — OnCompleted fires
/// last-registered-first — the last to run, after Cairn's callback (registered deeper, in the WithLinks
/// endpoint filter). Awaiting <see cref="WaitAsync"/> thus guarantees Cairn's diagnostic has already run.
/// </summary>
internal sealed class ResponseCompletion
{
    private readonly object _gate = new();
    private TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Registers the completion probe. Call before mapping endpoints so it wraps them.</summary>
    public void Use(IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            signal = _pending;
        }

        context.Response.OnCompleted(() =>
        {
            signal.TrySetResult();
            return Task.CompletedTask;
        });

        await next(context);
    });

    /// <summary>Waits for the in-flight response to finish (all OnCompleted callbacks run), then arms the next.</summary>
    public async Task WaitAsync(TimeSpan? timeout = null)
    {
        Task signal;
        lock (_gate)
        {
            signal = _pending.Task;
        }

        await signal.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

        lock (_gate)
        {
            _pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
