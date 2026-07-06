using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SystemOptimierer.Core
{
    /// <summary>
    /// ICommand für asynchrone Operationen.
    /// Vorteile gegenüber RelayCommand mit async-Lambda:
    ///  - Kein unkontrolliertes "async void": Exceptions werden abgefangen
    ///    und an <c>onException</c> weitergereicht statt die App zu beenden.
    ///  - Reentrancy-Schutz: solange die Operation läuft, ist CanExecute false,
    ///    Doppelklicks starten den Vorgang also nicht zweimal.
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private readonly Action<Exception>? _onException;
        private bool _isExecuting;

        public AsyncRelayCommand(
            Func<object?, Task> execute,
            Predicate<object?>? canExecute = null,
            Action<Exception>? onException = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onException = onException;
        }

        public bool CanExecute(object? parameter)
            => !_isExecuting && (_canExecute == null || _canExecute(parameter));

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;

            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                _onException?.Invoke(ex);
            }
            finally
            {
                _isExecuting = false;
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
