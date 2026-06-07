# Original User Request

## Initial Request — 2026-06-07T11:10:53+02:00

Eliminating duplicate code blocks in the Recovery Center, extending document extensions to all development and system files, and optimizing the selection of 10,000+ files using CollectionViewSource DeferRefresh.

Working directory: d:\sahma\Documents\GitHub\Systemoptimierungs_Windows
Integrity mode: development

## Requirements

### R1. Frontend Cleanups (MainWindow.xaml)
- Clean up the first column header and template in `RecoveryDataGrid`.
- Replace the leftmost `DataGridTemplateColumn` with the exact, clean XAML block featuring `IsAllFilesSelected` binding via `RelativeSource`.

### R2. Low-Level Recovery Carving (RecoveryService.cs)
- Confirm that `ScanPhysicalSectorsAsync` has exactly one sector search loop using `offset += 512`.
- Correct the syntax structure of `MatchCarvingSignatureOffset` and `EstimateCarvedFileSizeFromOffset` at the end of the file.
- Verify that document format filtering supports all development/system formats (`.cs`, `.json`, `.html`, `.docx`, `.pdf`, `.log`, `.lnk`).

### R3. Performance and VM Optimizations (MainViewModel.cs)
- Remove the appended duplicate class clone at the end of the file (everything after the second `using System;` occurrence).
- Clean up duplicate declarations of `deletedFiles` in `ScanRecoveryAsync` and maintain the proper hybrid-scan sequence.
- Optimize the `IsAllFilesSelected` property using the `CollectionViewSource.GetDefaultView` defer-refresh wrapper to handle mass selection (10,000+ items) without rendering lag.

### R4. Compilation & Rebuild
- Wipe the `bin` and `obj` directories.
- Build the solution with `dotnet build` and resolve any compile errors dynamically.

## Acceptance Criteria

### Performance & VM
- [ ] Bulk selection of 10,000+ items in `IsAllFilesSelected` is wrapped in `CollectionViewSource.GetDefaultView(RecoverableFiles).DeferRefresh()`.
- [ ] The solution compiles with exactly 0 errors and 0 warnings.
- [ ] No duplicate code blocks or extra namespace imports are left at the bottom of `MainViewModel.cs`.
