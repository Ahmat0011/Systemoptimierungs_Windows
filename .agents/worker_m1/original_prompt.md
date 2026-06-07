## 2026-06-07T09:13:15Z

You are teamwork_preview_worker for Milestone 1.
Your working directory is d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\.agents\worker_m1.
Your mission is to:
1. Read the scope at d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\.agents\sub_orch_m1\SCOPE.md.
2. Read the findings and recommendations in the Explorer handoff reports:
   - Explorer 1: d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\.agents\explorer_m1_1\handoff.md
   - Explorer 2: d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\.agents\explorer_m1_2\handoff.md
   - Explorer 3: d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\.agents\explorer_m1_3\handoff.md
3. Implement the cleanups in d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\MainWindow.xaml:
   - Clean up the first column header and template in RecoveryDataGrid.
   - Replace the leftmost DataGridTemplateColumn with the exact, clean XAML block featuring IsAllFilesSelected binding via RelativeSource.
   - Remove the redundant Path= prefix from the header CheckBox's binding.
   - Standardize the indentation of the leftmost column block to 28 spaces (matching sibling columns).
4. MANDATORY INTEGRITY WARNING:
   DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A Forensic Auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.
5. Build and verify the solution:
   - Perform clean rebuild: wipe 'bin' and 'obj' directories in the project.
   - Run 'dotnet build' from the root directory to confirm the build succeeds with exactly 0 errors and 0 warnings.
6. Write a handoff report to d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\.agents\worker_m1\handoff.md documenting your changes, verification commands, and results, then notify the parent.
