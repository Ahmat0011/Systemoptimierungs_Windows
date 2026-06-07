# Scope: Milestone 2 - R2. Low-Level Recovery Carving (RecoveryService.cs)

## Architecture
- `Services/RecoveryService.cs` implements scanning and file carving logic.

## Milestones
- Task: Confirm that `ScanPhysicalSectorsAsync` has exactly one sector search loop using `offset += 512`.
- Task: Correct the syntax structure of `MatchCarvingSignatureOffset` and `EstimateCarvedFileSizeFromOffset` at the end of the file.
- Task: Verify that document format filtering supports all development/system formats (`.cs`, `.json`, `.html`, `.docx`, `.pdf`, `.log`, `.lnk`).

## Interface Contracts
- `ScanDeletedFilesAsync` and `ScanPhysicalSectorsAsync` return `List<RecoverableFile>`.
