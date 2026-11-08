using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Core
{
    /// <summary>
    /// Führt eine Aktualisierung nie parallel aus und fasst gehäufte Anforderungen zusammen:
    /// Wer <see cref="RunAsync"/> aufruft, wartet höchstens auf einen Durchlauf, der NACH seiner
    /// Anforderung gestartet ist. Anforderungen, die ein solcher Durchlauf bereits abdeckt,
    /// lösen keinen weiteren (teuren) Scan aus.
    /// </summary>
    public sealed class CoalescingRefresh : IDisposable
    {
        private readonly Func<CancellationToken, Task> _refresh;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private long _requested;
        private long _completed;
        private int _disposed;

        public CoalescingRefresh(Func<CancellationToken, Task> refresh)
        {
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            long ticket = Interlocked.Increment(ref _requested);
            bool acquired = false;
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;

                if (Volatile.Read(ref _disposed) != 0) return;

                // Ein Durchlauf, der nach dieser Anforderung begann, hat sie bereits erledigt.
                if (Interlocked.Read(ref _completed) >= ticket) return;

                long coveredUpTo = Interlocked.Read(ref _requested);
                await _refresh(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _completed, coveredUpTo);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
                // Instanz wurde während des Wartens entsorgt.
            }
            finally
            {
                if (acquired && Volatile.Read(ref _disposed) == 0)
                {
                    try
                    {
                        _gate.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Ignorieren, falls Dispose() zeitgleich aufgerufen wurde.
                    }
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                _gate.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
