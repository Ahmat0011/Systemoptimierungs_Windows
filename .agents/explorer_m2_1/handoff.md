# Handoff Report: RecoveryService Analysis (Milestone 2 - Explorer 1)

This handoff report summarizes the read-only findings for `Services\RecoveryService.cs` in preparation for implementation changes in Milestone 2.

---

## 1. Observation
- **Sector Search Loop in `ScanPhysicalSectorsAsync`**: Located at lines 360–393 in `d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\Services\RecoveryService.cs`.
  ```csharp
  360:                         for (int offset = 0; offset < (int)bytesRead - 8; offset += 512)
  361:                         {
  ...
  364:                             string? foundExt = MatchCarvingSignatureOffset(buffer, offset, includeDocs, includeImages, includeVideos, includeMusic);
  365:                             if (foundExt != null)
  366:                             {
  367:                                 long startOffset = position + offset;
  368:                                 long sizeBytes = EstimateCarvedFileSizeFromOffset(buffer, offset, foundExt);
  ...
  ```
- **Syntax and Compilation Status**: Successfully built the solution using `dotnet build` with zero errors and zero warnings.
  ```
  Der Buildvorgang wurde erfolgreich ausgeführt.
      0 Warnung(en)
      0 Fehler
  ```
- **Structure and Logic of Carving Methods**:
  - `MatchCarvingSignatureOffset` is declared at line 480:
    ```csharp
    480:         private string? MatchCarvingSignatureOffset(byte[] block, int offset, bool includeDocs, bool includeImages, bool includeVideos, bool includeMusic)
    ```
    Inside `MatchCarvingSignatureOffset`, there is no usage of `includeMusic`, nor any matching for audio formats. Constants like `ID3_Header` (line 74) are never used.
  - `IsAsciiSector` at line 463 uses `Math.Min(block.Length - offset, 512)` instead of `bytesRead - offset` to bound check.
  - `EstimateCarvedFileSizeFromOffset` is declared at line 510:
    ```csharp
    510:         private long EstimateCarvedFileSizeFromOffset(byte[] block, int offset, string extension)
    ```
    This method has unused parameters `block` and `offset`, and uses hardcoded sizes for each file type (lines 514–520).
- **Document Extensions List**: Declared at line 50:
  ```csharp
  50:         private static readonly string[] DocumentExtensions = { ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".txt", ".rtf", ".odt", ".csv", ".cs", ".json", ".java", ".class", ".html", ".htm", ".config", ".inf", ".log", ".lnk", ".db", ".xml" };
  ```
  This array contains the requested `.cs`, `.json`, `.html`, `.docx`, `.pdf`, `.log`, and `.lnk` extensions.
- **Physical Sector Carving Matching Support**:
  In `MatchCarvingSignatureOffset`, only `.pdf` (line 486), `.xlsx`/`.pptx`/`.docx` (lines 488–495), and `.txt` via ASCII check (line 496) are processed when `includeDocs` is true.

---

## 2. Logic Chain
- **Confirmation of Sector Search Loop**:
  We traced the execution path in `ScanPhysicalSectorsAsync` starting from line 278. There is exactly one sector search loop beginning at line 360 which uses the increment expression `offset += 512`. This satisfies the single sector loop requirement.
- **Syntax/Compilation Assessment**:
  Running `dotnet build` successfully confirms that there are no C# syntax errors or compiler warnings within `MatchCarvingSignatureOffset` or `EstimateCarvedFileSizeFromOffset`.
- **Identifying Structural Issues**:
  1. We compared `MatchCarvingSignatureOffset` parameters against its body. The parameter `includeMusic` is present in the signature but completely unreferenced in the method body, and no music files are matched.
  2. We compared the parameters of `EstimateCarvedFileSizeFromOffset` against its switch statement. The parameters `block` and `offset` are never referenced. Instead, fixed constants are returned.
  3. We analyzed the bounds checking logic for `IsAsciiSector` and `MatchCarvingSignatureOffset` (ZIP check). Because they check `block.Length - offset` (where `block` is the reused 25.6MB buffer) rather than checking the amount of active read data (`bytesRead - offset`), the methods will read stale data if the last block read is less than the buffer capacity.
- **Assessing Document Format Filtering Logic**:
  1. We confirmed that `DocumentExtensions` includes all seven requested formats: `.cs`, `.json`, `.html`, `.docx`, `.pdf`, `.log`, `.lnk`.
  2. In Recycle Bin and Temp scans, the allowed extension list is constructed directly from `DocumentExtensions`, so all formats are supported.
  3. In Raw Sector Carving, `MatchCarvingSignatureOffset` only matches `.pdf`, `.docx`, `.xlsx`, `.pptx`, and generic `.txt` (via ASCII). Binary formats like `.lnk` or specific text formats like `.cs`, `.json`, `.html`, `.log` are not checked or mapped to their proper extensions. Therefore, sector carving does not fully support these formats.

---

## 3. Caveats
- **Run-time Permissions**: Physical sector carving requires Administrative privileges to open the physical drive handles (`\\.\PhysicalDriveX`). In a non-elevated context, `CreateFile` will return an invalid handle and log a warning, falling back to dummy file generation during restore. This is expected behavior on Windows.
- **No Performance Test Done**: Since this is a read-only investigation, run-time performance of the sector scan was not profiled under actual load, but static analysis indicates it performs a synchronous `Task.Delay(5)` in each block iteration to prevent UI freeze/thread starvation.

---

## 4. Conclusion
- The sector loop in `ScanPhysicalSectorsAsync` is unique and correctly aligns to 512-byte boundaries, but suffers from a buffer overrun logic flaw that reads stale data at the end of the scan.
- The syntax structures of `MatchCarvingSignatureOffset` and `EstimateCarvedFileSizeFromOffset` are syntactically valid (compile with 0 errors), but have major structural deficiencies: `includeMusic` is ignored, audio headers are unused, and size estimation parameters are ignored in favor of hardcoded sizes.
- Document format filtering covers all requested extensions in high-level scans, but raw sector carving fails to support `.lnk` (ignored entirely) and `.cs`, `.json`, `.html`, `.log` (recovered incorrectly as `.txt`).

---

## 5. Verification Method
- **Verify single sector loop**: Inspect lines 360–393 in `Services\RecoveryService.cs` using a viewer tool to confirm it is the only loop using `offset += 512`.
- **Verify compilation**: Run `dotnet build` in `d:\sahma\Documents\GitHub\Systemoptimierungs_Windows` to ensure the project compiles with 0 errors.
- **Verify structural issues**: Inspect the body of `MatchCarvingSignatureOffset` and `EstimateCarvedFileSizeFromOffset` (lines 480–522) to confirm parameter usage and missing music mapping logic.
