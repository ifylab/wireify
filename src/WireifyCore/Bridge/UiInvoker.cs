// SPDX-License-Identifier: Apache-2.0
using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Rhino;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Runs a document-touching call on Grasshopper's UI thread. The MCP host serves requests on
    /// background threads, but Rhino/Grasshopper document access is not thread-safe.
    /// </summary>
    public interface IUiInvoker
    {
        T Invoke<T>(Func<T> func);
        void Invoke(Action action);
    }

    /// <summary>Runs inline on the calling thread — headless tests, or callers already on the UI thread.</summary>
    public sealed class InlineUiInvoker : IUiInvoker
    {
        public T Invoke<T>(Func<T> func) => func();
        public void Invoke(Action action) => action();
    }

    /// <summary>
    /// UI-thread dispatch with a bounded PICKUP wait: if the UI thread does not start the callback
    /// within the timeout, the caller gets a clear "Rhino is busy" error instead of a silent hang,
    /// and the callback is atomically abandoned — it can never execute late (a mutation must not
    /// fire after its failure was reported). Once started, execution may take as long as it needs
    /// (a legitimate long solve is not a timeout). The post primitive is injected so the state
    /// machine is unit-testable without Rhino.
    /// </summary>
    public sealed class BoundedUiInvoker : IUiInvoker
    {
        readonly Action<Action> _post;
        readonly Func<bool> _invokeRequired;
        readonly TimeSpan _pickupTimeout;

        const int Pending = 0, Running = 1, Abandoned = 2;

        public BoundedUiInvoker(Action<Action> post, Func<bool> invokeRequired, TimeSpan? pickupTimeout = null)
        {
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _invokeRequired = invokeRequired ?? throw new ArgumentNullException(nameof(invokeRequired));
            _pickupTimeout = pickupTimeout ?? TimeSpan.FromSeconds(15);
        }

        public T Invoke<T>(Func<T> func)
        {
            if (!_invokeRequired()) return func();

            T result = default!;
            ExceptionDispatchInfo? captured = null;
            var state = Pending;
            // Not disposed deliberately: a raced late callback may still touch them, and a
            // per-call allocation at wireify call rates is nothing.
            var started = new ManualResetEventSlim(false);
            var done = new ManualResetEventSlim(false);

            _post(() =>
            {
                // Exactly one of {Running, Abandoned} wins the CAS — a callback that lost to
                // abandonment returns without executing anything.
                if (Interlocked.CompareExchange(ref state, Running, Pending) != Pending) return;
                started.Set();
                try { result = func(); }
                catch (Exception ex) { captured = ExceptionDispatchInfo.Capture(ex); }
                finally { done.Set(); }
            });

            if (!started.Wait(_pickupTimeout))
            {
                if (Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending)
                    throw new TimeoutException(
                        $"Rhino's UI thread did not pick this call up within {_pickupTimeout.TotalSeconds:0}s — " +
                        "Rhino is busy or blocked (a long solve, a modal dialog, or a hung operation). " +
                        "The call was NOT executed. Let Rhino go idle, then retry once.");
                // The callback won the race and is running — fall through and wait it out.
            }

            done.Wait();
            captured?.Throw();
            return result;
        }

        public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });
    }

    /// <summary>
    /// Marshals onto the Rhino UI thread via <c>RhinoApp.InvokeOnUiThread</c> through
    /// <see cref="BoundedUiInvoker"/> (bounded pickup + abandoned-callback guard), with the
    /// re-entrancy check (<c>RhinoApp.InvokeRequired</c>) so an already-UI-thread call runs inline.
    /// </summary>
    public sealed class RhinoUiInvoker : IUiInvoker
    {
        readonly BoundedUiInvoker _inner;

        public RhinoUiInvoker(TimeSpan? pickupTimeout = null)
        {
            _inner = new BoundedUiInvoker(
                action => RhinoApp.InvokeOnUiThread(action),
                () => RhinoApp.InvokeRequired,
                pickupTimeout);
        }

        public T Invoke<T>(Func<T> func) => _inner.Invoke(func);
        public void Invoke(Action action) => _inner.Invoke(action);
    }
}
