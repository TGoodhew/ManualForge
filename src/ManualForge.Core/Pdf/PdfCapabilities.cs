using PdfSharp.Pdf.IO;

namespace ManualForge.Core.Pdf;

/// <summary>Why a file cannot be written to directly.</summary>
public enum ModificationBlocker
{
    /// <summary>Nothing in the way.</summary>
    None,

    /// <summary>Owner-password permissions. Flattening clears it.</summary>
    OwnerPassword,

    /// <summary>A user password is set, so the content itself cannot be read without it.</summary>
    UserPassword,

    /// <summary>A digital signature. Modifying would invalidate it.</summary>
    Signature,

    /// <summary>The file is malformed beyond what lenient parsing can recover.</summary>
    Corrupt,

    /// <summary>Something else stopped the file opening.</summary>
    Unknown,
}

public sealed record PdfCapabilities(
    string Path,
    bool CanOpenForImport,
    bool CanOpenForModify,
    ModificationBlocker Blocker,
    bool HasEncryptDictionary,
    bool HasSignature,
    int PageCount,
    string? Detail)
{
    /// <summary>True when the file can be written to only after being flattened into a fresh document.</summary>
    public bool NeedsFlattening => !CanOpenForModify && CanOpenForImport;

    /// <summary>True when nothing can be done with the file at all.</summary>
    public bool IsHopeless => !CanOpenForImport;
}

/// <summary>
/// Works out, up front, whether a file can be written to and what stands in the way.
///
/// This runs before any work is done on a document, because discovering a blocker halfway through
/// a 400-page manual wastes an hour of GPU time. On this corpus it is not an edge case: 150 of 725
/// files refuse modification, 148 of them behind an owner password.
/// </summary>
public static class PdfInspector
{
    public static PdfCapabilities Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var hasEncrypt = HasEncryptDictionary(path);
        var hasSignature = HasSignatureMarker(path);

        var canImport = false;
        var canModify = false;
        var pageCount = 0;
        var blocker = ModificationBlocker.None;
        string? detail = null;

        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            canImport = true;
            pageCount = document.PageCount;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            blocker = ClassifyFailure(ex, hasEncrypt);
        }

        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            canModify = true;
            if (pageCount == 0)
                pageCount = document.PageCount;
        }
        catch (Exception ex)
        {
            detail ??= ex.Message;
            if (blocker == ModificationBlocker.None)
                blocker = ClassifyFailure(ex, hasEncrypt);
        }

        // A signature does not stop PDFsharp writing, but it does mean writing would invalidate
        // the signature, so it counts as a blocker for our purposes.
        if (canModify && hasSignature)
        {
            blocker = ModificationBlocker.Signature;
            detail ??= "The document carries a digital signature, which modification would invalidate.";
        }

        return new PdfCapabilities(
            path, canImport, canModify, blocker, hasEncrypt, hasSignature, pageCount, detail);
    }

    private static ModificationBlocker ClassifyFailure(Exception ex, bool hasEncrypt)
    {
        var message = ex.Message;

        if (message.Contains("owner password", StringComparison.OrdinalIgnoreCase))
            return ModificationBlocker.OwnerPassword;
        if (message.Contains("user password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("password is required to open", StringComparison.OrdinalIgnoreCase))
            return ModificationBlocker.UserPassword;
        if (message.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase))
            return ModificationBlocker.Corrupt;
        if (message.Contains("password", StringComparison.OrdinalIgnoreCase) && hasEncrypt)
            return ModificationBlocker.OwnerPassword;

        return ModificationBlocker.Unknown;
    }

    /// <summary>
    /// Looks for an /Encrypt entry. It lives in the trailer, which neither library exposes
    /// conveniently, and a byte scan is both reliable enough and faster than a full parse.
    /// </summary>
    public static bool HasEncryptDictionary(string path) => ContainsBytes(path, "/Encrypt"u8);

    /// <summary>Looks for the markers a signature field leaves behind.</summary>
    public static bool HasSignatureMarker(string path)
        => ContainsBytes(path, "/SigFlags"u8) || ContainsBytes(path, "/Type/Sig"u8) || ContainsBytes(path, "/Type /Sig"u8);

    private static bool ContainsBytes(string path, ReadOnlySpan<byte> needle)
    {
        try
        {
            // Read in overlapping chunks so the needle is never split across a boundary, and so a
            // 60 MB manual does not land in memory whole.
            const int chunkSize = 1 << 20;
            using var stream = File.OpenRead(path);
            var overlap = needle.Length - 1;
            var buffer = new byte[chunkSize + overlap];
            var carried = 0;

            while (true)
            {
                var read = stream.Read(buffer, carried, chunkSize);
                if (read <= 0)
                    return false;

                var available = carried + read;
                if (buffer.AsSpan(0, available).IndexOf(needle) >= 0)
                    return true;

                if (available <= overlap)
                    return false;

                buffer.AsSpan(available - overlap, overlap).CopyTo(buffer);
                carried = overlap;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
