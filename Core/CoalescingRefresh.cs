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
    public sealed class CoalescingRefresh
    {
        private readonly Func<CancellationToken, Task> _refresh;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private long _requested;
        private long _completed;

        public CoalescingRefresh(Func<CancellationToken, Task> refresh)
        {
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            long ticket = Interlocked.Increment(ref _requested);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // Ein Durchlauf, der nach dieser Anforderung begann, hat sie bereits erledigt.
                if (Interlocked.Read(ref _completed) >= ticket) return;

                long coveredUpTo = Interlocked.Read(ref _requested);
                await _refresh(cancellationToken);
                Interlocked.Exchange(ref _completed, coveredUpTo);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
