# Investigation Report: RecoveryService.cs Analysis

This report details the investigation of the `RecoveryService.cs` implementation for Milestone 2, addressing sector search loops, method syntax structure, and document format filtering logic.

---

## 1. Sector Search Loop in `ScanPhysicalSectorsAsync`

### Direct Observation
The physical sector scan is implemented in `ScanPhysicalSectorsAsync` (lines 278-420). The sector search loop starts at line 360:

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
...
392:                             }
393:                         }
```

### Analysis & Findings
- **Loop Quantity**: There is exactly **one** sector search loop in `ScanPhysicalSectorsAsync` that steps through the drive buffer using `offset += 512`. No duplicates of this loop were found in the method or the wider class.
- **Buffer Bound Vulnerability (Logical Flaw)**:
  - The drive reading loop uses a shared buffer of size 25.6 MB (`byte[] buffer = new byte[25600000]`).
  - At the end of the drive, the actual read bytes (`bytesRead`, line 354) might be less than `buffer.Length`.
  - Inside `MatchCarvingSignatureOffset` (called on line 364), the boundary check is:
    ```csharp
    482:             int remaining = block.Length - offset;
    483:             if (remaining < 8) return null;
    ```
    Since `block` is the `buffer` reference, `block.Length` is always `25600000` (the array capacity) regardless of `bytesRead`.
    In line 490, for ZIP-based files (`.docx`, `.xlsx`, `.pptx`), the limit is calculated as:
    ```csharp
    490:                     int searchLimit = Math.Min(remaining, 1024);
    491:                     string asciiString = Encoding.ASCII.GetString(block, offset, searchLimit);
    ```
    If `offset` is close to the end of the read data (`bytesRead`), `remaining` remains large because it is calculated against `block.Length`. Consequently, `searchLimit` will be `1024` and `Encoding.ASCII.GetString` will read past `bytesRead` into stale data left in the buffer from the previous block read. This can lead to false positives or incorrect classifications.
- **Unused Parameters**: The loop passes `buffer` and `offset` to `EstimateCarvedFileSizeFromOffset(buffer, offset, foundExt)` on line 368. However, as analyzed below, these parameters are completely ignored by the target method.

---

## 2. Syntax and Structural Review of End Methods

### Direct Observation
The helper methods `MatchCarvingSignatureOffset` and `EstimateCarvedFileSizeFromOffset` are implemented at the end of the file (lines 480-523):

```csharp
480:         private string? MatchCarvingSignatureOffset(byte[] block, int offset, bool includeDocs, bool includeImages, bool includeVideos, bool includeMusic)
481:         {
...
508:         }
509: 
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

### Analysis & Findings
- **Compilation Check**: The solution builds without errors or warnings (`0 Warnung(en)`, `0 Fehler` via `dotnet build`). The syntax is valid C# 10+.
- **Structural Issues**:
  - **Unused Parameters in `EstimateCarvedFileSizeFromOffset`**: `byte[] block` and `int offset` are declared in the method signature but are never referenced in the body. The method estimates file size purely based on the extension string. This represents a minor signature pollution and unnecessary overhead.
  - **Extensibility Limitations**: The signatures and patterns are hardcoded within standard conditional structures. Adding more formats requires manually modifying internal nested conditionals inside `MatchCarvingSignatureOffset`.
  - **ZIP-Carving Fallback Limitation**: For zip archives (`.zip` headers used by `.docx`, `.xlsx`, `.pptx`), if the ascii string does not contain `"xl/"` or `"ppt/"`, it defaults to returning `.docx` (line 494). This fails to identify standard `.zip` files or generic zip-based formats, and falsely classifies them all as Word documents.

---

## 3. Document Format Filtering Logic

### Direct Observation
The supported extensions are defined in `DocumentExtensions` on line 50:

```csharp
50:         private static readonly string[] DocumentExtensions = { ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".txt", ".rtf", ".odt", ".csv", ".cs", ".json", ".java", ".class", ".html", ".htm", ".config", ".inf", ".log", ".lnk", ".db", ".xml" };
```

And mapped in `ScanDeletedFilesAsync` (lines 115-120) and `DetermineFileTypeLabel` (lines 731-743).

### Analysis & Findings
- **File System / Recycle Bin / Temp Folder Scan (`ScanDeletedFilesAsync`)**:
  - Supports all requested development/system formats.
  - Verification:
    - `.cs`: Present in `DocumentExtensions` (Index 11).
    - `.json`: Present in `DocumentExtensions` (Index 12).
    - `.html`: Present in `DocumentExtensions` (Index 15).
    - `.docx`: Present in `DocumentExtensions` (Index 1).
    - `.pdf`: Present in `DocumentExtensions` (Index 0).
    - `.log`: Present in `DocumentExtensions` (Index 19).
    - `.lnk`: Present in `DocumentExtensions` (Index 20).
- **Physical Sector Carving (`MatchCarvingSignatureOffset` / `ScanPhysicalSectorsAsync`)**:
  - There is a mismatch. If `includeDocs` is true, physical carving *only* identifies `.pdf`, `.docx`, `.xlsx`, `.pptx`, and general plain text as `.txt`.
  - **Missing / Unsupported Carving Signatures**:
    - **`.lnk`**: Not supported. The Shell Link Binary File Format (magic bytes `4C 00 00 00` at the start of sector) is not implemented in `MatchCarvingSignatureOffset`. These files will never be detected or carved during a physical scan.
    - **`.cs`, `.json`, `.html`, `.log`**: These plain-text files do not have specific checks. If they match the ASCII threshold check in `IsAsciiSector`, they will all be carved with a generic `.txt` extension rather than their actual format extensions.
