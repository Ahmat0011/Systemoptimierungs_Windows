# Analysis Report: RecoveryService.cs Code Review

**Summary**: RecoveryService.cs successfully compiles with no errors, but has structural and logic gaps: a buffer-bound stale data leak exists in the raw sector search loop, music file format signatures are declared but never used in carving, and document format classification ignores specific development and system extensions (.cs, .json, .html, .log, .lnk), mapping them as generic text or omitting them entirely.

---

## 1. Physical Sector Search Loop Analysis

### Sector Search Loop Location
The sector scanning loop in `ScanPhysicalSectorsAsync` is located at **lines 360–393**. 
This is **exactly one** sector search loop using the standard 512-byte physical sector step size (`offset += 512`) in the entire method and file.

```csharp
360:                         for (int offset = 0; offset < (int)bytesRead - 8; offset += 512)
361:                         {
362:                             if (ct.IsCancellationRequested) break;
363: 
364:                             string? foundExt = MatchCarvingSignatureOffset(buffer, offset, includeDocs, includeImages, includeVideos, includeMusic);
365:                             if (foundExt != null)
366:                             {
367:                                 long startOffset = position + offset;
368:                                 long sizeBytes = EstimateCarvedFileSizeFromOffset(buffer, offset, foundExt);
369:                                 
370:                                 string fileType = DetermineFileTypeLabel(foundExt);
371:                                 string name = $"Rekonstruiert_{fileCounter:D3}{foundExt}";
372:                                 
373:                                 log($"-> [GEFUNDEN] {foundExt.ToUpper()} Dateisignatur bei Sektor {(startOffset / 512)}! (Größe: {FormatSize(sizeBytes)})");
374:                                 
375:                                 var newFile = new RecoverableFile
376:                                 {
377:                                     Name = name,
378:                                     FileType = fileType,
379:                                     Size = FormatSize(sizeBytes),
380:                                     OriginalPath = $@"{cleanedDrive}\Sektor_{(startOffset / 512)}",
381:                                     DateDeleted = $"Sektor {(startOffset / 512)} (Magic Header)",
382:                                     SourcePath = $"RAW_CARVE|{cleanedDrive}|{startOffset}|{sizeBytes}|{foundExt}",
383:                                     IsSelected = false
384:                                 };
385: 
386:                                 lock (files)
387:                                 {
388:                                     files.Add(newFile);
389:                                 }
390:                                 onFileFound?.Invoke(newFile);
391:                                 fileCounter++;
392:                             }
393:                         }
```

### Loop Issues and Vulnerabilities
- **Stale Data Leak on Partial Buffer Read**: In the loop header, the upper boundary checks `offset < (int)bytesRead - 8`. However, `MatchCarvingSignatureOffset` calculates `remaining` based on the static array size `block.Length - offset` (where `block` is the full `25.6 MB` buffer) instead of `bytesRead - offset`. On a partial read (e.g., end of drive), if it matches a signature, it may scan up to `1024` bytes, pulling in stale data from a previous read buffer iteration.
  - *Proton Remedy*: Change the signature of `MatchCarvingSignatureOffset` to take `(int)bytesRead` (or slice the buffer) to correctly calculate the valid remaining byte length.

---

## 2. Syntax & Structure of Key Carving Methods

### `MatchCarvingSignatureOffset` Analysis
Located at **lines 480–508**. The syntax structure compiles correctly but suffers from structural and logic bugs:

| Issue | Description | Affected Lines |
|---|---|---|
| **Ignored Music Formats** | The boolean parameter `includeMusic` is completely ignored. There is no block checking for audio file magic bytes, meaning music carving is broken in the physical scan. | 480, 507 |
| **Generic Text Fallback** | Any plain text/ASCII file (such as `.cs`, `.json`, `.html`, or `.log`) falls back directly to returning `.txt` because `IsAsciiSector` is evaluated first. | 496 |
| **Missing Windows Shortcuts** | There is no logic checking for Windows Shortcut files (`.lnk`). | 484–497 |

```csharp
480:         private string? MatchCarvingSignatureOffset(byte[] block, int offset, bool includeDocs, bool includeImages, bool includeVideos, bool includeMusic)
481:         {
...
496:                 if (IsAsciiSector(block, offset)) return ".txt";
...
507:             return null;
508:         }
```

### `EstimateCarvedFileSizeFromOffset` Analysis
Located at **lines 510–522**. Currently compiles but uses high-level fallback for all unlisted extensions:

```csharp
510:         private long EstimateCarvedFileSizeFromOffset(byte[] block, int offset, string extension)
511:         {
512:             switch (extension.ToLowerInvariant())
513:             {
514:                 case ".png": return 1500000;
515:                 case ".jpg": return 1200000;
516:                 case ".pdf": return 2500000;
517:                 case ".mp4": return 25000000;
518:                 case ".docx": return 1200000;
519:                 case ".xlsx": return 1500000;
520:                 default: return 800000;
521:             }
522:         }
```
- **Overestimating File Sizes**: New formats and plain-text files all fallback to `800,000` bytes (800 KB). For shortcut files (`.lnk` usually < 4KB) or code files (`.cs` usually < 50KB), this is extremely inefficient.

---

## 3. Document Format Filtering Support

We reviewed the support for all required development and system formats: `.cs`, `.json`, `.html`, `.docx`, `.pdf`, `.log`, and `.lnk`.

1. **DocumentExtensions Definition (Supported)**:
   At line 50, all required formats are included in the array:
   ```csharp
   private static readonly string[] DocumentExtensions = { ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".txt", ".rtf", ".odt", ".csv", ".cs", ".json", ".java", ".class", ".html", ".htm", ".config", ".inf", ".log", ".lnk", ".db", ".xml" };
   ```
2. **Metadata Scans (Supported)**:
   `ScanDeletedFilesAsync` correctly handles them via the Recycle Bin metadata search and User Temp scan using `allowedExtensions.UnionWith(DocumentExtensions)`.
3. **Physical Carving Scan (Missing/Incomplete)**:
   - **`.pdf` and `.docx`**: Supported via explicit signature checking.
   - **`.cs`, `.json`, `.html`, and `.log`**: Not specifically supported; they are all matched as generic `.txt` via `IsAsciiSector`.
   - **`.lnk`**: Not supported at all; no checks exist for the shortcut file signature (`0x4C 0x00 0x00 0x00`).

---

## 4. Proposed Fixes (Patch Details)

A `.patch` file has been generated at `d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\.agents\explorer_m2_3\RecoveryService.cs.patch` containing precise, machine-applicable changes to:
1. Pass `bytesRead` to `MatchCarvingSignatureOffset` to compute valid `remaining` bounds.
2. Add specific checks for `.lnk` (using Shell Link magic headers), `.cs`, `.json`, `.html`, and `.log` (by checking starting ASCII sequences).
3. Implement `includeMusic` format signature carving for `.mp3`, `.wav`, `.flac`, and `.ogg`.
4. Provide appropriate default carved sizes for each format inside `EstimateCarvedFileSizeFromOffset` (e.g. 4KB for `.lnk`, 50KB for `.cs`, 100KB for `.json`/`.txt`).
