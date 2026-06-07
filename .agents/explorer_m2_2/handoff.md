# Handoff Report — Explorer 2 (Milestone 2)

This handoff report summarizes the findings of the read-only investigation on `Services\RecoveryService.cs` regarding physical sector loops, method structures, and format filtering logic.

---

## 1. Observation
- **Physical Sector Loop**: 
  - File: `d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\Services\RecoveryService.cs`
  - Exact lines: 360-393
  - Loop construct: `for (int offset = 0; offset < (int)bytesRead - 8; offset += 512)`
  - Exact call to signature matching: `string? foundExt = MatchCarvingSignatureOffset(buffer, offset, includeDocs, includeImages, includeVideos, includeMusic);`
  - Exact call to size estimation: `long sizeBytes = EstimateCarvedFileSizeFromOffset(buffer, offset, foundExt);`
- **Method Structures**:
  - File: `d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\Services\RecoveryService.cs`
  - Exact lines: 480-523 for `MatchCarvingSignatureOffset` and `EstimateCarvedFileSizeFromOffset`.
  - Signature `EstimateCarvedFileSizeFromOffset`: `private long EstimateCarvedFileSizeFromOffset(byte[] block, int offset, string extension)` (lines 510-522). Parameters `block` and `offset` are never used in the body.
  - Boundary check in `MatchCarvingSignatureOffset`: `int remaining = block.Length - offset;` (line 482).
- **Format Filtering**:
  - File: `d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\Services\RecoveryService.cs`
  - Exact line: 50
  - List of documents: `private static readonly string[] DocumentExtensions = { ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".txt", ".rtf", ".odt", ".csv", ".cs", ".json", ".java", ".class", ".html", ".htm", ".config", ".inf", ".log", ".lnk", ".db", ".xml" };`
  - Physical carving checks: lines 484-497 in `MatchCarvingSignatureOffset` only check signatures for `.pdf`, `.xlsx`, `.pptx`, `.docx`, and generic ASCII text via `IsAsciiSector` (returning `.txt`). No signature checks exist for `.cs`, `.json`, `.html`, `.log`, or `.lnk`.
- **Compilation Check**:
  - Command run: `dotnet build` from workspace root.
  - Result: Build succeeded with `0 Warnung(en)`, `0 Fehler` (successful completion).

---

## 2. Logic Chain
1. **Loop Confirmations**:
   - By viewing lines 360-393 in `RecoveryService.cs`, it is confirmed that `ScanPhysicalSectorsAsync` contains exactly one sector search loop utilizing `offset += 512`. No duplicate loops exist.
2. **Buffer Overread Vulnerability**:
   - In `ScanPhysicalSectorsAsync`, a shared buffer (`byte[] buffer = new byte[25600000]`) is allocated.
   - When calling `MatchCarvingSignatureOffset(buffer, offset, ...)`, `buffer` is passed as `block`.
   - On line 482, `remaining = block.Length - offset` is calculated using the capacity of the buffer (`block.Length` = `25600000`) instead of the count of valid bytes read (`bytesRead`).
   - If `offset` is close to the end of the valid data (`bytesRead`), `remaining` is still computed as a large number.
   - In lines 490-491, `Encoding.ASCII.GetString(block, offset, searchLimit)` is called with `searchLimit = Math.Min(remaining, 1024)`.
   - This causes it to read beyond `bytesRead` into stale data left from previous iterations of the `while` loop, leading to potential false matches or data misinterpretation.
3. **Syntax / Structure Review**:
   - The code compiles successfully, confirming there are no syntax or compilation errors.
   - `EstimateCarvedFileSizeFromOffset` declares `block` and `offset` parameters that are unused, which is a structural clean-up issue.
4. **Format Verification**:
   - Comparing `DocumentExtensions` to the required formats: `.cs`, `.json`, `.html`, `.docx`, `.pdf`, `.log`, and `.lnk` are all listed, confirming full support for logical file/recycle bin scans.
   - However, in physical sector carving:
     - `.lnk` has no magic signature matching, making it completely unrecoverable.
     - `.cs`, `.json`, `.html`, and `.log` have no specific signature logic. If they pass the ASCII test, they are recovered with a generic `.txt` extension, altering the original file format.

---

## 3. Caveats
- The investigation was purely read-only, as required by the role constraints. No code changes were implemented.
- We did not write physical disks/drives or run actual drive scans because administrator permissions and raw disks are required. However, the logical verification of the code path is self-contained.

---

## 4. Conclusion
- **Sector Search Loop**: Exactly one loop is present, but it suffers from a logical buffer boundaries flaw where it evaluates stale bytes near the end of a sector read block.
- **Syntax**: Valid and compiles successfully; contains minor structure clean-up issues (unused parameters).
- **Format Filtering**: Works for directory scans, but fails to recover `.lnk` files during physical sector scanning, and lumps `.cs`, `.json`, `.html`, and `.log` files under `.txt`.

---

## 5. Verification Method
- **Compilation Check**: Run `dotnet build` from `d:\sahma\Documents\GitHub\Systemoptimierungs_Windows` to verify clean compilation.
- **Code Inspection**:
  - Open `d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\Services\RecoveryService.cs`.
  - Go to line 360 to verify the loop structure and bounds.
  - Go to line 482 to verify the buffer length logic error (`block.Length` vs `bytesRead`).
  - Go to line 510 to verify unused parameters in `EstimateCarvedFileSizeFromOffset`.
  - Go to line 50 and 484 to inspect supported extensions vs signature carving limits.
