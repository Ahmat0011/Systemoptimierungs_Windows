# Review Report — RecoveryService.cs

## Review Summary

**Verdict**: APPROVE

The implementation of file carving and sector-based recovery in `RecoveryService.cs` has been successfully verified. The developer resolved the buffer bounds bug in the sector search loop, implemented robust carving checks for all required formats (including `.lnk` CLSID validation, plain-text heuristics, and multiple audio containers), and confirmed that the solution compiles cleanly with zero warnings and zero errors.

---

## Findings

No critical or major findings were discovered that would prevent approval. A minor observation is detailed below:

### Minor Finding 1: Heuristic Sensitivity for Non-English Plaintext Files
- **What**: The helper method `IsAsciiSector` uses a strict 98% density check for ASCII bytes (range 32–126, plus control characters 9, 10, 13) to classify a sector as plain-text.
- **Where**: `Services/RecoveryService.cs`, lines 463–478.
- **Why**: Plain-text files in other languages (such as German with umlauts like ä, ö, ü) or files containing heavy UTF-8 multibyte characters may fall below the 98% threshold, causing the sector to be skipped for plaintext detection.
- **Suggestion**: Consider lowering the threshold slightly (e.g., to 95%) or checking for common UTF-8 byte sequences if multilingual recovery is a primary requirement. For code and configuration files, the current 98% threshold is completely sufficient.

---

## Verified Claims

- **Claim 1**: `ScanPhysicalSectorsAsync` has exactly one sector search loop using `offset += 512`.
  - *Verification Method*: Inspected the method body of `ScanPhysicalSectorsAsync` in `RecoveryService.cs`.
  - *Result*: **PASS**. Exactly one loop `for (int offset = 0; offset < (int)bytesRead - 8; offset += 512)` handles the sector scanning.
- **Claim 2**: `MatchCarvingSignatureOffset` and `EstimateCarvedFileSizeFromOffset` compile cleanly and their syntax structure is correct.
  - *Verification Method*: Ran `dotnet build` on the solution.
  - *Result*: **PASS**. The project compiles with 0 errors and 0 warnings.
- **Claim 3**: Document format filtering supports all required development/system formats (.cs, .json, .html, .docx, .pdf, .log, .lnk).
  - *Verification Method*: Inspected `DocumentExtensions` array and `MatchCarvingSignatureOffset` document formats switch block.
  - *Result*: **PASS**. All 7 formats are in `DocumentExtensions` and explicitly matched in code.
- **Claim 4**: The sector search loop buffer bounds bug is fixed (preventing reads past bytesRead).
  - *Verification Method*: Inspected how `remaining = validBytes - offset` is defined and used.
  - *Result*: **PASS**. Every lookup check validates that the array index accessed is strictly less than `validBytes` via checking `remaining` boundaries or `Math.Min(remaining, limit)`.
- **Claim 5**: Carving support is correctly implemented for `.lnk` (including CLSID checks), plain-text files (.cs, .json, .html, .log), and music files (.mp3, .wav, .flac, .ogg).
  - *Verification Method*: Checked binary signature arrays and parsing logic for shell links, text headers, and audio files.
  - *Result*: **PASS**. The `.lnk` CLSID validation perfectly checks the CLSID block offsets, and audio headers cover both container headers and frame headers (e.g., ID3 / frame-sync for MP3, RIFF+WAVE for WAV, fLaC for FLAC, OggS for OGG).
- **Claim 6**: Perform a compilation check using `dotnet build` to confirm 0 errors and 0 warnings.
  - *Verification Method*: Executed `dotnet clean` followed by `dotnet build`.
  - *Result*: **PASS**. Succeeded with 0 errors and 0 warnings.

---

## Coverage Gaps

- **DeepRecoveryService.cs**: The current task focused exclusively on `RecoveryService.cs`. The corresponding changes in `DeepRecoveryService.cs` were not fully audited.
  - *Risk Level*: Low.
  - *Recommendation*: Accept risk, as `DeepRecoveryService.cs` is out of scope for this review slot.

---

## Unverified Items

- **Physical Drive Reading Behavior**: Access to live physical disks via `CreateFile` with raw paths (`\\.\PhysicalDriveX`) requires administrator privilege and actual raw sector layout. Live drive tests could not be run programmatically without local hardware interaction.
  - *Reason*: Not verified on a real physical drive due to virtualization boundaries.
  - *Mitigation*: The code is logically safe, handles file pointer seek/read failure gracefully, and uses fallback simulation if access fails.

---

## Adversarial Stress-Testing

### 1. Assumption Stress-Testing

- **Assumption challenged**: The physical disk size (`totalSize`) returned by `GetPhysicalDriveSize` is accurate and non-zero.
  - *Attack Scenario*: If WMI fails, or query returns 0, the fallback of 100 GB is used. If the actual drive is smaller (e.g., a 16 GB USB drive), we will attempt to read up to 100 GB.
  - *Blast Radius*: Without safety checks, this could lead to infinite errors or exceptions when reading beyond the disk limits.
  - *Mitigation*: The loop checks `readSuccess` and `bytesRead == 0` (line 355) to exit early when reading past the end of the disk. This protects the scanner from crashing or infinite-looping.

- **Assumption challenged**: The index calculations in `MatchCarvingSignatureOffset` never overflow or read out of bounds.
  - *Attack Scenario*: An offset close to `bytesRead` is processed.
  - *Blast Radius*: IndexOutOfRangeException, crashing the recovery scan.
  - *Mitigation*: The loop terminates at `bytesRead - 8`. Furthermore, every block check has safety assertions: `remaining >= 20` (for `.lnk`), `remaining >= 12` (for `.wav`), or `Math.Min(remaining, ...)` (for zip/text search limits). This prevents index overflow.

### 2. Edge Case Mining

- **Zero-length buffer**: If `bytesRead` is returned as `0` by the operating system, the loop condition `offset < (int)bytesRead - 8` is evaluated as `offset < -8`. Since `offset = 0`, the loop body is bypassed entirely. No out-of-bounds access occurs.
- **Short sector read**: If `bytesRead` is `512` (exactly one sector), `bytesRead - 8 = 504`. The loop executes for `offset = 0`, then increments by `512` to `512`. Since `512 < 504` is false, it terminates correctly.
