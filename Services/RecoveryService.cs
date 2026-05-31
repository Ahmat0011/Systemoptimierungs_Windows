using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Models;

namespace SystemOptimierer.Services
{
    public class RecoveryService : IRecoveryService
    {
        public List<string> GetAvailableDrives()
        {
            var drives = new List<string>();
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady)
                    {
                        drives.Add(drive.Name);
                    }
                }
            }
            catch
            {
                drives.Add(@"C:\");
            }
            return drives;
        }

        public async Task<List<RecoverableFile>> ScanDeletedFilesAsync(
            string driveLetter,
            bool includeDocs,
            bool includeImages,
            bool includeVideos,
            bool includeMusic,
            Action<string> log,
            CancellationToken ct)
        {
            var foundFiles = new List<RecoverableFile>();
            
            // Set up allowed extensions
            var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (includeDocs)
            {
                allowedExtensions.UnionWith(new[] { ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".pdf", ".txt", ".rtf", ".odt" });
            }
            if (includeImages)
            {
                allowedExtensions.UnionWith(new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".tiff" });
            }
            if (includeVideos)
            {
                allowedExtensions.UnionWith(new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm" });
            }
            if (includeMusic)
            {
                allowedExtensions.UnionWith(new[] { ".mp3", ".wav", ".wma", ".ogg", ".flac", ".aac", ".m4a" });
            }

            if (allowedExtensions.Count == 0)
            {
                log("[WARNUNG] Keine Dateitypen zur Suche ausgewählt.");
                return foundFiles;
            }

            // 1. Scan $Recycle.Bin of the selected drive
            string recycleBinPath = Path.Combine(driveLetter, "$Recycle.Bin");
            log($"Scanne Papierkorb-Ressourcen unter '{recycleBinPath}'...");
            
            if (Directory.Exists(recycleBinPath))
            {
                await Task.Run(() =>
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(recycleBinPath);
                        // Scan all subdirectories (SIDs)
                        foreach (var sidDir in dirInfo.GetDirectories())
                        {
                            if (ct.IsCancellationRequested) return;
                            try
                            {
                                // Recursively find all $R files
                                foreach (var file in sidDir.GetFiles("$R*", SearchOption.AllDirectories))
                                {
                                    if (ct.IsCancellationRequested) return;
                                    
                                    string ext = file.Extension;
                                    if (allowedExtensions.Contains(ext))
                                    {
                                        // Look for the corresponding $I file (metadata)
                                        string iFileName = "$I" + file.Name.Substring(2);
                                        string? iFilePath = null;
                                        if (file.DirectoryName != null)
                                        {
                                            iFilePath = Path.Combine(file.DirectoryName, iFileName);
                                        }

                                        string originalPath = string.Empty;
                                        long size = file.Length;
                                        DateTime deletionTime = file.LastWriteTime;

                                        if (iFilePath != null && File.Exists(iFilePath))
                                        {
                                            var parsed = ParseIFile(iFilePath);
                                            if (!string.IsNullOrEmpty(parsed.OriginalPath))
                                            {
                                                originalPath = parsed.OriginalPath;
                                                size = parsed.Size;
                                                deletionTime = parsed.DeletionTime;
                                            }
                                        }

                                        if (string.IsNullOrEmpty(originalPath))
                                        {
                                            originalPath = Path.Combine(driveLetter, "Gelöscht", file.Name.Substring(2));
                                        }

                                        string fileTypeLabel = DetermineFileTypeLabel(ext);

                                        lock (foundFiles)
                                        {
                                            foundFiles.Add(new RecoverableFile
                                            {
                                                Name = Path.GetFileName(originalPath),
                                                OriginalPath = originalPath,
                                                Size = FormatSize(size),
                                                DateDeleted = deletionTime != DateTime.MinValue ? deletionTime.ToString("dd.MM.yyyy HH:mm") : "Unbekannt",
                                                FileType = fileTypeLabel,
                                                SourcePath = file.FullName,
                                                IsSelected = false
                                            });
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Ignore SID access errors
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log($"[Info] Fehler beim Zugriff auf den Papierkorb: {ex.Message}");
                    }
                }, ct);
            }
            else
            {
                log("[Info] Kein Papierkorb-Verzeichnis auf diesem Laufwerk gefunden.");
            }

            // 2. If C:\ drive, scan User Temp directories as well for lost temporary files
            if (driveLetter.StartsWith("C", StringComparison.OrdinalIgnoreCase))
            {
                string tempPath = Path.GetTempPath();
                log($"Scanne temporäre Verzeichnisse unter '{tempPath}' nach wiederherstellbaren Zwischenspeicherungen...");
                
                await Task.Run(() =>
                {
                    try
                    {
                        var tempDir = new DirectoryInfo(tempPath);
                        // Search files modified in the last 14 days
                        var cutoff = DateTime.Now.AddDays(-14);
                        
                        foreach (var file in tempDir.GetFiles("*", SearchOption.TopDirectoryOnly))
                        {
                            if (ct.IsCancellationRequested) return;
                            try
                            {
                                if (allowedExtensions.Contains(file.Extension) && file.LastWriteTime >= cutoff)
                                {
                                    string fileTypeLabel = DetermineFileTypeLabel(file.Extension);
                                    lock (foundFiles)
                                    {
                                        // Avoid duplicate names or sources
                                        if (!foundFiles.Any(f => f.SourcePath.Equals(file.FullName, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            foundFiles.Add(new RecoverableFile
                                            {
                                                Name = file.Name,
                                                OriginalPath = file.FullName,
                                                Size = FormatSize(file.Length),
                                                DateDeleted = file.LastWriteTime.ToString("dd.MM.yyyy HH:mm") + " (Temp-Cache)",
                                                FileType = fileTypeLabel,
                                                SourcePath = file.FullName,
                                                IsSelected = false
                                            });
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Ignore locked file errors in temp
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log($"[Info] Fehler beim Scannen des Temp-Verzeichnisses: {ex.Message}");
                    }
                }, ct);
            }

            return foundFiles.OrderByDescending(f => f.DateDeleted).ToList();
        }

        public async Task RestoreFilesAsync(
            List<RecoverableFile> files,
            string targetDirectory,
            Action<string> log,
            CancellationToken ct)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                        log($"[System] Zielverzeichnis '{targetDirectory}' wurde erstellt.");
                    }

                    int successCount = 0;
                    int index = 1;

                    foreach (var file in files)
                    {
                        if (ct.IsCancellationRequested) break;
                        
                        try
                        {
                            if (!File.Exists(file.SourcePath))
                            {
                                log($"[Fehler {index}/{files.Count}] Quelldatei existiert nicht mehr: '{file.Name}'");
                                index++;
                                continue;
                            }

                            string targetFileName = file.Name;
                            string targetPath = Path.Combine(targetDirectory, targetFileName);

                            // Resolve naming conflicts cleanly to protect files
                            int counter = 1;
                            while (File.Exists(targetPath))
                            {
                                string nameWithoutExt = Path.GetFileNameWithoutExtension(targetFileName);
                                string ext = Path.GetExtension(targetFileName);
                                targetPath = Path.Combine(targetDirectory, $"{nameWithoutExt} ({counter}){ext}");
                                counter++;
                            }

                            log($"[Schritt {index}/{files.Count}] Kopiere '{file.Name}' nach '{Path.GetFileName(targetPath)}'...");
                            
                            // Safe file copy
                            File.Copy(file.SourcePath, targetPath, true);
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            log($"[Fehler {index}/{files.Count}] Bei Datei '{file.Name}': {ex.Message}");
                        }
                        index++;
                    }

                    log($"\nWiederherstellung beendet: {successCount} von {files.Count} Datei(en) erfolgreich gerettet.");
                }
                catch (Exception ex)
                {
                    log($"[FEHLER] Kritischer Fehler im Wiederherstellungsprozess: {ex.Message}");
                }
            }, ct);
        }

        private (string OriginalPath, long Size, DateTime DeletionTime) ParseIFile(string iFilePath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(iFilePath);
                // Windows 10/11 $I file header length is at least 28 bytes
                if (bytes.Length < 28)
                {
                    return (string.Empty, 0, DateTime.MinValue);
                }

                long size = BitConverter.ToInt64(bytes, 8);
                long fileTime = BitConverter.ToInt64(bytes, 16);
                
                DateTime deletionTime = DateTime.MinValue;
                try
                {
                    deletionTime = DateTime.FromFileTime(fileTime);
                }
                catch {}

                string originalPath = string.Empty;
                if (bytes.Length > 28)
                {
                    originalPath = Encoding.Unicode.GetString(bytes, 28, bytes.Length - 28).TrimEnd('\0');
                }

                return (originalPath, size, deletionTime);
            }
            catch
            {
                return (string.Empty, 0, DateTime.MinValue);
            }
        }

        private string DetermineFileTypeLabel(string extension)
        {
            extension = extension.ToLowerInvariant();
            if (new[] { ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".pdf", ".txt", ".rtf", ".odt" }.Contains(extension))
                return "Dokumente";
            if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".tiff" }.Contains(extension))
                return "Bilder";
            if (new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm" }.Contains(extension))
                return "Videos";
            if (new[] { ".mp3", ".wav", ".wma", ".ogg", ".flac", ".aac", ".m4a" }.Contains(extension))
                return "Musik";
            return "Andere";
        }

        private string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return $"{(double)bytes / (1024 * 1024 * 1024):F1} GB";
            if (bytes >= 1024L * 1024)
                return $"{(double)bytes / (1024 * 1024):F1} MB";
            if (bytes >= 1024L)
                return $"{(double)bytes / 1024L:F1} KB";
            return $"{bytes} B";
        }
    }
}
