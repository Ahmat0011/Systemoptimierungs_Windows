using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace SystemOptimierer.Core
{
    /// <summary>
    /// ICommand für asynchrone Operationen.
    /// Vorteile gegenüber RelayCommand mit async-Lambda:
    ///  - Kein unkontrolliertes "async void": Exceptions werden abgefangen
    ///    und an <c>onException</c> weitergereicht statt die App zu beenden.
    ///  - Atomarer Reentrancy-Schutz: solange die Operation läuft, ist CanExecute false,
    ///    Doppelklicks starten den Vorgang also garantiert nicht zweimal.
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private readonly Action<Exception>? _onException;
        private int _isExecuting;

        public AsyncRelayCommand(
            Func<object?, Task> execute,
            Predicate<object?>? canExecute = null,
            Action<Exception>? onException = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onException = onException;
        }

        public AsyncRelayCommand(
            Func<Task> execute,
            Func<bool>? canExecute = null,
            Action<Exception>? onException = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = _ => execute();
            _canExecute = canExecute != null ? _ => canExecute() : null;
            _onException = onException;
        }

        public bool IsExecuting => Volatile.Read(ref _isExecuting) != 0;

        public bool CanExecute(object? parameter)
            => !IsExecuting && (_canExecute == null || _canExecute(parameter));

        public async void Execute(object? parameter)
        {
            if (!_canExecute?.Invoke(parameter) ?? false) return;
            if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return;

            RaiseCanExecuteChanged();
            try
            {
                await _execute(parameter);
            }
            catch (OperationCanceledException)
            {
                // Geplanter Abbruch durch CancellationToken – kein Fehler.
            }
            catch (Exception ex)
            {
                _onException?.Invoke(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        public static void RaiseCanExecuteChanged()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(CommandManager.InvalidateRequerySuggested);
            }
            else
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
