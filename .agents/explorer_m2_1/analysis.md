# Investigation Report: RecoveryService Analysis

**Target File:** `d:\sahma\Documents\GitHub\Systemoptimierungs_Windows\Services\RecoveryService.cs`  
**Explorer:** Explorer 1 (Milestone 2)  
**Date:** 2026-06-07  

---

## Executive Summary
This report details the read-only investigation of `RecoveryService.cs` to analyze physical sector carving, signature matching, file size estimation, and document format filtering logic. 

Key findings include:
- **Sector Search Loop:** Confirmed exactly one loop in `ScanPhysicalSectorsAsync` stepping by `offset += 512`. A potential logic bug exists where stale buffer data is read when the remaining bytes in the buffer are less than the check limits.
- **Compilation/Syntax Status:** The project compiles with 0 errors and 0 warnings. No compilation or syntax issues are present in `MatchCarvingSignatureOffset` or `EstimateCarvedFileSizeFromOffset`.
- **Structural Issues:** `MatchCarvingSignatureOffset` completely ignores the `includeMusic` parameter, missing the implementation for all music headers defined in the class. `EstimateCarvedFileSizeFromOffset` has unused parameters and returns crude, hardcoded size estimates.
- **Document Format Filtering:** The filter array `DocumentExtensions` includes all required development and system extensions (`.cs`, `.json`, `.html`, `.docx`, `.pdf`, `.log`, `.lnk`). However, the raw sector carving logic (`MatchCarvingSignatureOffset`) does not support carving for most of these formats individually; they are either mapped generically to `.txt` or ignored completely.

---

## Detailed Findings

### 1. Verification of the Sector Search Loop in `ScanPhysicalSectorsAsync`
We verified that `ScanPhysicalSectorsAsync` contains **exactly one** sector search loop that increments by `offset += 512`.

* **Location:** Lines 360–393
* **Code Snippet:**
```csharp
for (int offset = 0; offset < (int)bytesRead - 8; offset += 512)
{
    if (ct.IsCancellationRequested) break;

    string? foundExt = MatchCarvingSignatureOffset(buffer, offset, includeDocs, includeImages, includeVideos, includeMusic);
    if (foundExt != null)
    {
        long startOffset = position + offset;
        long sizeBytes = EstimateCarvedFileSizeFromOffset(buffer, offset, foundExt);
        
        string fileType = DetermineFileTypeLabel(foundExt);
        string name = $"Rekonstruiert_{fileCounter:D3}{foundExt}";
        
        log($"-> [GEFUNDEN] {foundExt.ToUpper()} Dateisignatur bei Sektor {(startOffset / 512)}! (Größe: {FormatSize(sizeBytes)})");
        
        var newFile = new RecoverableFile
        {
            Name = name,
            FileType = fileType,
            Size = FormatSize(sizeBytes),
            OriginalPath = $@"{cleanedDrive}\Sektor_{(startOffset / 512)}",
            DateDeleted = $"Sektor {(startOffset / 512)} (Magic Header)",
            SourcePath = $"RAW_CARVE|{cleanedDrive}|{startOffset}|{sizeBytes}|{foundExt}",
            IsSelected = false
        };

        lock (files)
        {
            files.Add(newFile);
        }
        onFileFound?.Invoke(newFile);
        fileCounter++;
    }
}
```

#### Identifed Issues / Anomalies in the Loop:
1. **Buffer Overrun Logic Bug (Stale Buffer Reads):**  
   The buffer size is 25,600,000 bytes. During the last block read, `bytesRead` can be significantly less than the buffer size. The loop correctly limits `offset < bytesRead - 8`. However, inside `MatchCarvingSignatureOffset`, the variable `remaining` is computed as `block.Length - offset` (using the fixed buffer size of 25.6MB) instead of `bytesRead - offset`.  
   Consequently, the ZIP check (line 490) uses `Math.Min(remaining, 1024)` which evaluates to `1024`, and reads bytes up to `offset + 1024`. If `bytesRead - offset < 1024`, the program reads stale bytes from the previous read cycle or zero-filled memory. Similarly, `IsAsciiSector` uses `Math.Min(block.Length - offset, 512)` which can read past `bytesRead`.
2. **Lack of Duplicates:**  
   There are no other sector-level loops using `offset += 512` in the class or in related classes like `DeepRecoveryService` (which delegates directly to `RecoveryService`).

---

### 2. Syntax and Structure Analysis of End Methods
We inspected the methods `MatchCarvingSignatureOffset` (lines 480–508) and `EstimateCarvedFileSizeFromOffset` (lines 510–522).

#### Compilation & Syntax Status:
* **Result:** No compilation errors or syntax warnings. The solution compiles successfully.

#### Structural & Logical Issues:
1. **Ignored `includeMusic` Parameter:**  
   `MatchCarvingSignatureOffset` accepts `includeMusic` but never checks it and has no carving rules or return paths for any music file formats.
2. **Unused Static Signatures:**  
   The class defines multiple static signatures for audio/video (e.g., `ID3_Header`, `MP3_FrameHeader`, `RIFF_Header`, `FLAC_Header`, `OGG_Header`, `WMV_Header`, `FLV_Header`, `MKV_Header` at lines 74–82) but never uses them in signature matching.
3. **ZIP Format Defaulting:**  
   If a ZIP signature (`0x50 0x4B 0x03 0x04`) is detected but does not contain `"xl/"` or `"ppt/"` in the first 1024 bytes, it defaults to returning `".docx"`. This incorrectly classifies general ZIP archives and other OpenDocument formats (e.g., `.odt`) as Word documents.
4. **Unused Parameters in Size Estimation:**  
   `EstimateCarvedFileSizeFromOffset(byte[] block, int offset, string extension)` defines `block` and `offset` as parameters, but never uses them.
5. **Static/Hardcoded Size Estimation:**  
   Sizes are entirely hardcoded by file extension (e.g., 2.5 MB for PDF, 1.2 MB for JPG). This leads to carved files being truncated if they are larger, or bloated with garbage sectors if they are smaller.

---

### 3. Document Format Filtering Analysis
We analyzed the filtering logic to verify support for development and system formats: `.cs`, `.json`, `.html`, `.docx`, `.pdf`, `.log`, `.lnk`.

#### Supported Extensions in Matrix:
The static `DocumentExtensions` array (line 50) includes all the required extensions:
```csharp
private static readonly string[] DocumentExtensions = { 
    ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".txt", ".rtf", ".odt", ".csv", 
    ".cs", ".json", ".java", ".class", ".html", ".htm", ".config", ".inf", ".log", ".lnk", ".db", ".xml" 
};
```

#### Support Status by Scan Mode:

| File Extension | Supported in `ScanDeletedFilesAsync` (Recycle Bin/Temp) | Supported in `ScanPhysicalSectorsAsync` (Carving) | Notes / Details |
|---|---|---|---|
| **.cs** | Yes | No (Recovered as `.txt`) | Matched in deleted files. In carving, it falls back to `.txt` via ASCII check. |
| **.json** | Yes | No (Recovered as `.txt`) | Matched in deleted files. In carving, it falls back to `.txt` via ASCII check. |
| **.html** | Yes | No (Recovered as `.txt`) | Matched in deleted files. In carving, it falls back to `.txt` via ASCII check. |
| **.docx** | Yes | Yes | Explicitly matched in carving via ZIP + subfolder search. |
| **.pdf** | Yes | Yes | Explicitly matched in carving via `%PDF` signature. |
| **.log** | Yes | No (Recovered as `.txt`) | Matched in deleted files. In carving, it falls back to `.txt` via ASCII check. |
| **.lnk** | Yes | No | LNK is binary and has no signature matching in `MatchCarvingSignatureOffset`, making it impossible to carve. |

#### Updates Needed for Raw Carving Support:
To fully support carving the requested formats:
- **`.lnk`**: Add check for LNK magic header (`0x4C 0x00 0x00 0x00` at offset 0).
- **`.cs`, `.json`, `.html`, `.log`**: Add custom logic to distinguish them from standard `.txt` files (e.g., scanning the ASCII block for common indicators like `using `, `{`, `<html>`, or timestamps).
- **OLE Documents (`.doc`, `.xls`, `.ppt`)**: Add check for `OLE_Header` (`0xD0 0xCF 0x11 0xE0 0xA1 0xB1 0x1A 0xE1`).
- **Rich Text (`.rtf`)**: Add check for `RTF_Header` (`0x7B 0x5C 0x72 0x74 0x66`).
- **XML/SVG (`.xml`)**: Add check for `XML_Header` (`0x3C 0x3F 0x78 0x6D 0x6C`).
