// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Threading;

namespace WireifyCore.Hosting
{
    /// <summary>
    /// The default manifest watch behind <see cref="AppSurface"/>'s seam: a
    /// <see cref="FileSystemWatcher"/> on the home's <c>app/</c> dir, filtered to
    /// <c>manifest.json</c>, debounced (~200 ms) so an editor's save burst — including the
    /// write-temp-then-rename pattern — lands as one callback. Watching is best-effort by
    /// design: if it cannot start (no app dir yet, an exotic filesystem), edits still apply on
    /// the next recompute through the stream's per-solve manifest re-read.
    /// </summary>
    public static class ManifestWatch
    {
        static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

        public static IDisposable Start(string homeDir, Action changed)
        {
            if (changed is null) throw new ArgumentNullException(nameof(changed));
            var appDir = Path.Combine(homeDir, "app");
            // No app dir means no manifest to watch; the events stream that starts once an app
            // exists creates the watcher then.
            if (!Directory.Exists(appDir)) return EmptyDisposable.Instance;

            FileSystemWatcher watcher;
            Timer? debounce = null;
            try
            {
                watcher = new FileSystemWatcher(appDir, "manifest.json")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                        | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                debounce = new Timer(_ =>
                {
                    try { changed(); } catch { /* the surface handles its own errors */ }
                }, null, Timeout.Infinite, Timeout.Infinite);

                var timer = debounce;
                void Poke() { try { timer.Change(Debounce, Timeout.InfiniteTimeSpan); } catch { /* disposed */ } }
                watcher.Changed += (_, _) => Poke();
                watcher.Created += (_, _) => Poke();
                watcher.Deleted += (_, _) => Poke();
                watcher.Renamed += (_, _) => Poke();
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                debounce?.Dispose();
                return EmptyDisposable.Instance;
            }

            return new Handle(watcher, debounce);
        }

        sealed class Handle : IDisposable
        {
            FileSystemWatcher? _watcher;
            Timer? _debounce;

            public Handle(FileSystemWatcher watcher, Timer debounce)
            {
                _watcher = watcher;
                _debounce = debounce;
            }

            public void Dispose()
            {
                try { Interlocked.Exchange(ref _watcher, null)?.Dispose(); } catch { /* ignore */ }
                try { Interlocked.Exchange(ref _debounce, null)?.Dispose(); } catch { /* ignore */ }
            }
        }

        sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
