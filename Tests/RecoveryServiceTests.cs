using System;
using System.Reflection;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using SystemOptimierer.Services;

class Program
{
    private static MethodInfo matchMethod;
    private static MethodInfo estimateMethod;
    private static RecoveryService service;

    static int Main(string[] args)
    {
        service = new RecoveryService();
        var type = typeof(RecoveryService);
        matchMethod = type.GetMethod("MatchCarvingSignatureOffset", BindingFlags.NonPublic | BindingFlags.Instance);
        estimateMethod = type.GetMethod("EstimateCarvedFileSizeFromOffset", BindingFlags.NonPublic | BindingFlags.Instance);

        if (matchMethod == null || estimateMethod == null)
        {
            Console.WriteLine("FAIL: Private methods not found via reflection!");
            return 1;
        }

        bool allPassed = true;

        allPassed &= TestShellLinkCarving();
        allPassed &= TestTextFormatsDistinction();
        allPassed &= TestMusicFormatsCarving();
        allPassed &= TestSectorLoopBufferBoundsSafety();
        allPassed &= TestFileSizeEstimations();

        if (allPassed)
        {
            Console.WriteLine("\nALL TESTS PASSED SUCCESSFULLY!");
            return 0;
        }
        else
        {
            Console.WriteLine("\nSOME TESTS FAILED!");
            return 1;
        }
    }

    private static string InvokeMatch(byte[] block, int offset, int validBytes, bool includeDocs, bool includeImages, bool includeVideos, bool includeMusic)
    {
        var result = matchMethod.Invoke(service, new object[] { block, offset, validBytes, includeDocs, includeImages, includeVideos, includeMusic });
        return (string)result;
    }

    private static long InvokeEstimate(byte[] block, int offset, string extension)
    {
        return (long)estimateMethod.Invoke(service, new object[] { block, offset, extension });
    }

    // 1. Shell Link .lnk magic signature carving
    static bool TestShellLinkCarving()
    {
        Console.WriteLine("--- Test 1: Shell Link (.lnk) Carving ---");
        bool passed = true;

        // Shell Link Header starts with 0x0000004C (or 0x4C, 0x00, 0x00, 0x00)
        // Check LinkCLSID verification: bytes[4..7] = { 0x01, 0x14, 0x02, 0x00 }
        // bytes[16] = 0xC0, bytes[19] = 0x46
        byte[] validLnk = new byte[30];
        validLnk[0] = 0x4C; validLnk[1] = 0x00; validLnk[2] = 0x00; validLnk[3] = 0x00;
        validLnk[4] = 0x01; validLnk[5] = 0x14; validLnk[6] = 0x02; validLnk[7] = 0x00;
        validLnk[16] = 0xC0; validLnk[19] = 0x46;

        // Valid lnk, includeDocs = true -> should return ".lnk"
        string result1 = InvokeMatch(validLnk, 0, validLnk.Length, includeDocs: true, includeImages: false, includeVideos: false, includeMusic: false);
        if (result1 == ".lnk")
        {
            Console.WriteLine("Pass: Valid .lnk carving recognized.");
        }
        else
        {
            Console.WriteLine($"Fail: Valid .lnk carving recognized as: '{result1}'");
            passed = false;
        }

        // Valid lnk, includeDocs = false -> should return null
        string result2 = InvokeMatch(validLnk, 0, validLnk.Length, includeDocs: false, includeImages: false, includeVideos: false, includeMusic: false);
        if (result2 == null)
        {
            Console.WriteLine("Pass: .lnk carving ignored when includeDocs is false.");
        }
        else
        {
            Console.WriteLine($"Fail: .lnk carving not ignored when includeDocs is false (returned: '{result2}')");
            passed = false;
        }

        // Invalid lnk (incorrect magic bytes)
        byte[] invalidLnkMagic = (byte[])validLnk.Clone();
        invalidLnkMagic[0] = 0x4B; // changed 0x4C to 0x4B
        string result3 = InvokeMatch(invalidLnkMagic, 0, invalidLnkMagic.Length, includeDocs: true, includeImages: false, includeVideos: false, includeMusic: false);
        if (result3 == null)
        {
            Console.WriteLine("Pass: Invalid magic byte rejected.");
        }
        else
        {
            Console.WriteLine($"Fail: Invalid magic byte accepted as '{result3}'");
            passed = false;
        }

        // Invalid lnk (incorrect CLSID bytes)
        byte[] invalidLnkCLSID = (byte[])validLnk.Clone();
        invalidLnkCLSID[4] = 0x00; // changed 0x01 to 0x00
        string result4 = InvokeMatch(invalidLnkCLSID, 0, invalidLnkCLSID.Length, includeDocs: true, includeImages: false, includeVideos: false, includeMusic: false);
        if (result4 == null)
        {
            Console.WriteLine("Pass: Invalid CLSID bytes rejected.");
        }
        else
        {
            Console.WriteLine($"Fail: Invalid CLSID bytes accepted as '{result4}'");
            passed = false;
        }

        return passed;
    }

    // 2. Distinction of text formats (.cs, .json, .html, .log) under ASCII sector carving
    static bool TestTextFormatsDistinction()
    {
        Console.WriteLine("--- Test 2: Text Formats Distinction ---");
        bool passed = true;

        // We need 512 bytes for IsAsciiSector to work, or at least 128 bytes.
        // Let's create helper to make a 512 byte ASCII block containing a specific text at the start.
        byte[] MakeAsciiBlock(string content)
        {
            byte[] block = new byte[512];
            byte[] contentBytes = Encoding.ASCII.GetBytes(content);
            Array.Copy(contentBytes, block, Math.Min(contentBytes.Length, 512));
            // Fill the rest with spaces to satisfy 98% ASCII threshold (32 to 126, or 9, 10, 13)
            for (int i = contentBytes.Length; i < 512; i++)
            {
                block[i] = 32; // space
            }
            return block;
        }

        var testCases = new (string Name, string Content, string ExpectedExtension)[]
        {
            ("C# source", "using System;\nnamespace App {\n  public class Program {\n  }\n}", ".cs"),
            ("C# source with comment", "// This is a test comment\nclass A {}", ".cs"),
            ("JSON object", "{ \"name\": \"John\", \"age\": 30 }", ".json"),
            ("JSON array", "[ 1, 2, 3, 4 ]", ".json"),
            ("HTML document", "<!DOCTYPE html>\n<html><body></body></html>", ".html"),
            ("HTML tag", "<html>\n<head><title>Test</title></head>\n</html>", ".html"),
            ("Log message INFO", "[INFO] Application started at 12:00", ".log"),
            ("Log message WARN", "2026-06-07 [WARN] High memory usage detected", ".log"),
            ("Plain text file", "Hello World! This is just some random text file that has no code, logs, markup, or structured data.", ".txt")
        };

        foreach (var tc in testCases)
        {
            byte[] block = MakeAsciiBlock(tc.Content);
            string result = InvokeMatch(block, 0, block.Length, includeDocs: true, includeImages: false, includeVideos: false, includeMusic: false);
            if (result == tc.ExpectedExtension)
            {
                Console.WriteLine($"Pass: '{tc.Name}' recognized as {tc.ExpectedExtension}");
            }
            else
            {
                Console.WriteLine($"Fail: '{tc.Name}' recognized as '{result}' (expected {tc.ExpectedExtension})");
                passed = false;
            }

            // Also test if includeDocs = false returns null
            string resultNoDocs = InvokeMatch(block, 0, block.Length, includeDocs: false, includeImages: false, includeVideos: false, includeMusic: false);
            if (resultNoDocs == null)
            {
                Console.WriteLine($"Pass: '{tc.Name}' ignored when includeDocs is false.");
            }
            else
            {
                Console.WriteLine($"Fail: '{tc.Name}' was NOT ignored when includeDocs is false (returned '{resultNoDocs}')");
                passed = false;
            }
        }

        return passed;
    }

    // 3. Carving of music formats (.mp3, .wav, .flac, .ogg) when includeMusic is true
    static bool TestMusicFormatsCarving()
    {
        Console.WriteLine("--- Test 3: Music Formats Carving ---");
        bool passed = true;

        var musicCases = new (string Name, byte[] Content, string ExpectedExtension)[]
        {
            ("MP3 ID3 Header", new byte[] { 0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00 }, ".mp3"),
            ("MP3 Frame Header", new byte[] { 0xFF, 0xFB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, ".mp3"),
            ("WAV RIFH Header", new byte[] { 0x52, 0x49, 0x46, 0x48, 0x00, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 }, ".wav"),
            ("WAV RIFF Header", new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 }, ".wav"),
            ("FLAC Header (fLaC)", new byte[] { 0x66, 0x4C, 0x61, 0x63, 0x00, 0x00, 0x00, 0x00 }, ".flac"),
            ("FLAC Header (fLaC capital)", new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x00 }, ".flac"),
            ("OGG Header (OggS)", new byte[] { 0x4F, 0x67, 0x67, 0x53, 0x00, 0x00, 0x00, 0x00 }, ".ogg")
        };

        foreach (var tc in musicCases)
        {
            // Create a buffer larger than content to prevent any OOB
            byte[] block = new byte[32];
            Array.Copy(tc.Content, block, tc.Content.Length);

            // With includeMusic = true
            string result = InvokeMatch(block, 0, block.Length, includeDocs: false, includeImages: false, includeVideos: false, includeMusic: true);
            if (result == tc.ExpectedExtension)
            {
                Console.WriteLine($"Pass: {tc.Name} carved as {tc.ExpectedExtension}");
            }
            else
            {
                Console.WriteLine($"Fail: {tc.Name} carved as '{result}' (expected {tc.ExpectedExtension})");
                passed = false;
            }

            // With includeMusic = false
            string resultNoMusic = InvokeMatch(block, 0, block.Length, includeDocs: false, includeImages: false, includeVideos: false, includeMusic: false);
            if (resultNoMusic == null)
            {
                Console.WriteLine($"Pass: {tc.Name} ignored when includeMusic is false");
            }
            else
            {
                Console.WriteLine($"Fail: {tc.Name} NOT ignored when includeMusic is false (returned '{resultNoMusic}')");
                passed = false;
            }
        }

        return passed;
    }

    // 4. Sector loop buffer bounds safety
    static bool TestSectorLoopBufferBoundsSafety()
    {
        Console.WriteLine("--- Test 4: Sector Loop Buffer Bounds Safety ---");
        bool passed = true;

        // The goal here is to call MatchCarvingSignatureOffset with various offsets, validBytes, and buffer lengths to ensure NO IndexOutOfRangeException is thrown.
        // We will test empty blocks, very short blocks, and offsets close to bounds.

        byte[] smallBlock = new byte[16];
        // Fill it with potential headers to trigger inner checks
        smallBlock[0] = 0x4C; smallBlock[1] = 0x00; smallBlock[2] = 0x00; smallBlock[3] = 0x00; // lnk header but short CLSID
        smallBlock[4] = 0x52; smallBlock[5] = 0x49; smallBlock[6] = 0x46; smallBlock[7] = 0x46; // RIFF header but short Wave check

        for (int validBytes = 0; validBytes <= smallBlock.Length; validBytes++)
        {
            for (int offset = 0; offset <= validBytes; offset++)
            {
                try
                {
                    // Call match with all types enabled
                    string res = InvokeMatch(smallBlock, offset, validBytes, includeDocs: true, includeImages: true, includeVideos: true, includeMusic: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Fail: Exception thrown with validBytes={validBytes}, offset={offset}. Message: {ex.InnerException?.Message ?? ex.Message}");
                    passed = false;
                }
            }
        }

        // Also check ASCII check bounds safety (requires 128+ bytes remaining to even try, but let's test remaining = 127, 128, etc.)
        byte[] asciiBlock = new byte[200];
        for (int i = 0; i < asciiBlock.Length; i++) asciiBlock[i] = 65; // 'A'

        for (int validBytes = 0; validBytes <= asciiBlock.Length; validBytes++)
        {
            for (int offset = 0; offset <= validBytes; offset++)
            {
                try
                {
                    string res = InvokeMatch(asciiBlock, offset, validBytes, includeDocs: true, includeImages: true, includeVideos: true, includeMusic: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Fail: Exception thrown in ASCII check with validBytes={validBytes}, offset={offset}. Message: {ex.InnerException?.Message ?? ex.Message}");
                    passed = false;
                }
            }
        }

        if (passed)
        {
            Console.WriteLine("Pass: No out of bounds exceptions occurred under simulated boundary conditions.");
        }

        return passed;
    }

    // 5. File size estimations returned by EstimateCarvedFileSizeFromOffset
    static bool TestFileSizeEstimations()
    {
        Console.WriteLine("--- Test 5: File Size Estimations ---");
        bool passed = true;

        var testSizes = new (string Extension, long ExpectedSize)[]
        {
            (".png", 1500000),
            (".jpg", 1200000),
            (".pdf", 2500000),
            (".mp4", 25000000),
            (".docx", 1200000),
            (".xlsx", 1500000),
            (".cs", 50000),
            (".json", 100000),
            (".html", 200000),
            (".log", 1000000),
            (".lnk", 4096),
            (".pptx", 2000000),
            (".txt", 100000),
            (".mp3", 6000000),
            (".wav", 30000000),
            (".flac", 20000000),
            (".ogg", 5000000),
            (".unknown", 800000),
            (".XYZ", 800000)
        };

        byte[] dummyBlock = new byte[8];
        foreach (var ts in testSizes)
        {
            long estimated = InvokeEstimate(dummyBlock, 0, ts.Extension);
            if (estimated == ts.ExpectedSize)
            {
                Console.WriteLine($"Pass: '{ts.Extension}' size estimate is {estimated} bytes");
            }
            else
            {
                Console.WriteLine($"Fail: '{ts.Extension}' size estimate is {estimated} bytes (expected {ts.ExpectedSize})");
                passed = false;
            }
        }

        return passed;
    }
}
