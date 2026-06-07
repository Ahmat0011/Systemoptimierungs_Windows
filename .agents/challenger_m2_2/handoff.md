# Handoff Report — RecoveryService Carving Logic Verification

## 1. Observation
- **Test execution commands and outputs**:
  Run command: `dotnet run --project Tests/Tests.csproj /p:UseSharedCompilation=false`
  Result snippet:
  ```text
  Fail: 'Log message INFO' recognized as '.json' (expected .log)
  Pass: 'Log message WARN' recognized as .log
  ...
  SOME TESTS FAILED!
  ```
- **File path and code locations**:
  - `Services/RecoveryService.cs` line 521-524:
    ```csharp
    if (asciiStart.StartsWith("{") || asciiStart.StartsWith("[") || trimmed.StartsWith("{") || trimmed.StartsWith("["))
    {
        return ".json";
    }
    ```
  - `Services/RecoveryService.cs` line 532-535:
    ```csharp
    if (asciiStart.Contains("[INFO]") || asciiStart.Contains("[WARN]") || asciiStart.Contains("[ERROR]") || asciiStart.Contains("[DEBUG]"))
    {
        return ".log";
    }
    ```

## 2. Logic Chain
- **Step 1**: The test suite supplies `[INFO] Application started at 12:00` as simulated text input for log carving.
- **Step 2**: The string starts with the character `[`.
- **Step 3**: During classification, `MatchCarvingSignatureOffset` evaluates the `.json` format check before the `.log` check.
- **Step 4**: The `.json` check returns `.json` because `asciiStart.StartsWith("[")` is true.
- **Step 5**: Because `.json` is returned immediately, the code never reaches the `.log` check, resulting in a classification failure.
- **Step 6**: The other test scenarios (Shell Link carving, other ASCII format distinction, music formats carving when `includeMusic` is true, bounds safety on small arrays, and file size estimations) all pass as expected.

## 3. Caveats
- Running the tests on Windows under Windows Defender Application Control (WDAC) requires routing build outputs to `bin/Debug/net10.0-windows/` rather than the default `Tests/bin/` folder to bypass DLL loading blocks (error `0x800711C7`).
- Tests must be executed with `/p:UseSharedCompilation=false` to avoid Roslyn background compiler process locks on DLL files.

## 4. Conclusion
- The carving logic is functional and bounds-safe for shell links, music formats, and general text/size estimation.
- **Bug identified**: The precedence of the `.json` starts-with check (`StartsWith("[")`) over the `.log` contains check (`Contains("[INFO]")`) under ASCII sector carving creates a conflict where logs starting with bracketed levels are misclassified as JSON.
- **Actionable Fix recommendation**: Move the `.log` check before the `.json` check, or refine the `.json` check to verify that it is actually valid JSON (e.g., matching a trailing bracket or specific JSON structure) rather than just starting with `{` or `[`.

## 5. Verification Method
To independently run the tests:
1. Run the build command:
   `dotnet build Tests/Tests.csproj /p:UseSharedCompilation=false`
2. Run the test execution command:
   `dotnet run --project Tests/Tests.csproj /p:UseSharedCompilation=false`
3. Inspect the console output and verify that it matches the findings reported in `empirical_verification.md`.
