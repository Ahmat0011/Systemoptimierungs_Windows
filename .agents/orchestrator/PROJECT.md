# Project: Systemoptimierungs Windows

## Architecture
- WPF application for system optimization and file recovery.
- MainWindow.xaml: View layer displaying files.
- ViewModels/MainViewModel.cs: ViewModel containing application state, scan logic, and properties like `IsAllFilesSelected`.
- Services/RecoveryService.cs: Service implementing low-level signature scanning and file carving logic.

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| 1 | R1. Frontend Cleanups | Clean up RecoveryDataGrid first column template and header in MainWindow.xaml | None | IN_PROGRESS (a2826b8b-4c6a-4534-bf2d-9e3cfb3d8409) |
| 2 | R2. Low-Level Carving | Clean up and repair RecoveryService.cs functions and document format filtering | None | IN_PROGRESS (05199ecd-9251-44ba-8a55-8036980da107) |
| 3 | R3. VM Optimizations | Clean up duplicates and optimize IsAllFilesSelected selection with DeferRefresh in MainViewModel.cs | M1, M2 | PLANNED |
| 4 | R4. Compilation & Rebuild | Clean bin/obj, build solution, and verify correctness | M3 | PLANNED |

## Interface Contracts
### MainViewModel ↔ MainWindow
- `IsAllFilesSelected` is bound to the Header of the first DataGridTemplateColumn in MainWindow.xaml.
- `RecoverableFiles` is the source collection for RecoveryDataGrid.

### MainViewModel ↔ RecoveryService
- `ScanRecoveryAsync` utilizes `RecoveryService` scanning capabilities.

## Code Layout
- MainWindow: `MainWindow.xaml`
- RecoveryService: `Services/RecoveryService.cs`
- MainViewModel: `ViewModels/MainViewModel.cs`
