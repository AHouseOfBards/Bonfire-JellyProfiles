using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Profiles.Configuration
{
    /// <summary>
    /// Every piece of work Bonfire starts off a request thread, so that it stops when the
    /// server does.
    ///
    /// <para><b>Why.</b> Jellyfin restarts inside the same process (<c>Program.cs</c> loops
    /// <c>StartServer</c> while <c>_restartOnShutdown</c>; Campground-Coordination #33). The
    /// new host loads a new copy of Bonfire, and the old copy's background work carried on
    /// beside it: the picker's sweep twenty seconds after start, the session listener's second
    /// policy pass five seconds after a sign-in. Their delays waited on nothing a stop cancels,
    /// so they woke up after the restart and ran against services the old host had already
    /// disposed. The audit log and emails were written by both copies, each with its own
    /// static lock, which is no lock at all between the two.</para>
    ///
    /// <para><b>How.</b> <see cref="Run"/> hands the work the token a stop cancels and keeps
    /// the task until it ends. <see cref="Stopper"/> is the first hosted service registered,
    /// so the host stops it last, after the others have unhooked their events; it cancels the
    /// token and waits for what is still running, up to <see cref="StopWait"/>. Work asked for
    /// after that is not started: a later copy is about to take over.</para>
    ///
    /// <para>tests/cs/shutdown fails on any <c>Task.Run</c> in the plugin outside this file.</para>
    /// </summary>
    internal static class BackgroundWork
    {
        /// <summary>The longest a stop waits for running work.</summary>
        internal static readonly TimeSpan StopWait = TimeSpan.FromSeconds(10);

        private static readonly CancellationTokenSource Stopping = new();
        private static readonly ConcurrentDictionary<Task, byte> Running = new();

        /// <summary>Set by <see cref="Stopper"/> when the host starts; where a failed task is logged.</summary>
        internal static ILogger? Log = null;

        /// <summary>Cancelled when the server stops.</summary>
        internal static CancellationToken Token => Stopping.Token;

        /// <summary>Whether a stop has begun.</summary>
        internal static bool IsStopping => Stopping.IsCancellationRequested;

        /// <summary>
        /// Starts <paramref name="work"/> on the thread pool and tracks it until it ends. After a
        /// stop has begun, nothing is started and a completed task is returned.
        /// </summary>
        internal static Task Run(Func<CancellationToken, Task> work)
        {
            if (Stopping.IsCancellationRequested) return Task.CompletedTask;
            var token = Stopping.Token;
            var task = Task.Run(() => work(token));
            Running[task] = 0;
            // Removed when it ends, so the set holds only what is still running. A failure is
            // read here and logged: most callers drop the task, and an unread failure is lost.
            task.ContinueWith(
                t =>
                {
                    Running.TryRemove(t, out _);
                    if (t.IsFaulted)
                    {
                        Log?.LogWarning(t.Exception?.GetBaseException(), "ProfilesPlugin: Background work failed.");
                    }
                },
                TaskScheduler.Default);
            return task;
        }

        /// <summary>The same for work that does not wait on anything.</summary>
        internal static Task Run(Action<CancellationToken> work)
            => Run(token => { work(token); return Task.CompletedTask; });

        /// <summary>
        /// Cancels the token and waits for running work, up to <see cref="StopWait"/> or until
        /// <paramref name="cancellationToken"/> (the host's own shutdown timeout) gives up.
        /// Never throws: a task that failed was logged when it ended.
        /// </summary>
        internal static async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!Stopping.IsCancellationRequested) Stopping.Cancel();
            var running = Running.Keys.ToArray();
            if (running.Length == 0) return;
            var all = Task.WhenAll(running);
            try
            {
                await Task.WhenAny(all, Task.Delay(StopWait, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            // Observe the result so a failed or cancelled task is not reported as unobserved.
            _ = all.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }

        /// <summary>
        /// Stops <see cref="BackgroundWork"/> with the server. Registered before every other
        /// hosted service, so it is stopped after all of them.
        /// </summary>
        internal sealed class Stopper : IHostedService
        {
            public Stopper(ILogger<Stopper> logger) => BackgroundWork.Log = logger;

            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => BackgroundWork.StopAsync(cancellationToken);
        }
    }
}
