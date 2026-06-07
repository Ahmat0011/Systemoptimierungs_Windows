# Scope: Milestone 1 - R1. Frontend Cleanups (MainWindow.xaml)

## Architecture
- MainWindow.xaml contains the `RecoveryDataGrid` which displays the list of recoverable files.

## Milestones
- Task: Clean up the first column header and template in `RecoveryDataGrid`.
- Task: Replace the leftmost `DataGridTemplateColumn` with the exact, clean XAML block featuring `IsAllFilesSelected` binding via `RelativeSource`.

## Interface Contracts
- The leftmost column checkbox binds to `IsAllFilesSelected` on the MainViewModel (DataContext of DataGrid).
- The checkbox inside cells binds to `IsSelected` on individual `RecoverableFile` models.
