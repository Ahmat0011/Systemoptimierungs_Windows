# Implementation Plan

## Milestone 1: R1. Frontend Cleanups (MainWindow.xaml)
- **Objective**: Clean up the first column header and template in `RecoveryDataGrid` and replace the leftmost template column with `IsAllFilesSelected` binding.
- **Steps**:
  1. Explore: Analyze `MainWindow.xaml` to locate the target `RecoveryDataGrid` and its first column.
  2. Implement: Replace the leftmost column with `IsAllFilesSelected` checkbox binding.
  3. Review: Verify that XAML is syntax-valid and UI bindings are correct.

## Milestone 2: R2. Low-Level Recovery Carving (RecoveryService.cs)
- **Objective**: Clean up and repair `Services/RecoveryService.cs` (sector search loops, syntax errors, and document format filtering).
- **Steps**:
  1. Explore: Inspect `Services/RecoveryService.cs` for syntax issues in `MatchCarvingSignatureOffset` and `EstimateCarvedFileSizeFromOffset`, ensure loop uses `offset += 512`, and check format filters.
  2. Implement: Correct syntax, verify step sizes, and expand the supported extensions.
  3. Review: Verify that logic is clean and types compile correctly.

## Milestone 3: R3. Performance and VM Optimizations (MainViewModel.cs)
- **Objective**: Clean up duplicate code in `ViewModels/MainViewModel.cs` and optimize selection performance.
- **Steps**:
  1. Explore: Inspect `ViewModels/MainViewModel.cs` to locate duplicates at the end of the file, check `ScanRecoveryAsync` for duplicate `deletedFiles` declarations, and examine the mass selection code.
  2. Implement: Remove trailing duplicates, clean up double declarations, and implement DeferRefresh optimization.
  3. Review: Verify view model structure and check correctness.

## Milestone 4: R4. Compilation & Rebuild (Verification)
- **Objective**: Ensure the solution builds successfully and audit the code.
- **Steps**:
  1. Build: Wipe `bin` and `obj`, run `dotnet build`, resolve any compilation issues.
  2. Audit: Spawn Forensic Auditor to run integrity checks and verify authentic implementation.
