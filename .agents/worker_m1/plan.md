# Implementation Plan — MainWindow.xaml Cleanups

This plan details the steps for cleaning up the first column in the `RecoveryDataGrid` within `MainWindow.xaml`.

## Target File
`d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\MainWindow.xaml`

## Steps

### Step 1: Baseline Build Verification
- Confirm that the project builds in its current state (baseline).
- Run `dotnet build` from the root directory.

### Step 2: Code Modification in MainWindow.xaml
- Locate `RecoveryDataGrid` leftmost column starting at line 1439 up to line 1455.
- Replace the `DataGridTemplateColumn` block with a clean, standardized version:
  - Exact indentation of 28 spaces for the outer tag.
  - Consistent nested 4-space tab stops for children.
  - Header `CheckBox` `IsChecked` binding: `{Binding DataContext.IsAllFilesSelected, RelativeSource={RelativeSource AncestorType=DataGrid}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}`.
  - Notice the removal of the redundant `Path=` prefix in the binding path.
  - Sibling columns matching: the starting tag of this column must align with the starting tag of sibling columns like `<DataGridTextColumn Header="Dateiname" ... />` at line 1456.

### Step 3: Clean Rebuild
- Delete the `bin` and `obj` directories in the project root to ensure a completely clean build.
- Run `dotnet build` again.
- Verify that it compiles successfully with exactly 0 errors and 0 warnings.

### Step 4: Verification of Indentation and Formatting
- Re-read `MainWindow.xaml` to verify that the indentation is exactly 28 spaces for the outer tag and nested structure is correct.

### Step 5: Handoff and Notification
- Write the handoff report `d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\.agents\worker_m1\handoff.md`.
- Send a message to the parent agent with the status and path to the handoff report.
