namespace ManualForge.Core.Ocr;

/// <summary>
/// Remembers what recognition produced for a page, so an interrupted document does not have to be
/// recognised again from the beginning.
///
/// The insight is about which half of the work is expensive. Recognising a 639-page manual takes
/// thirteen minutes on the GPU; assembling the PDF from results already in hand takes seconds. So
/// there is no need for the writer to resume mid-document, which would mean appending a text layer
/// to a partly-built file and reasoning about which pages already carry one. Cache the recognition
/// instead, rebuild the document from scratch each attempt, and an interruption costs the page in
/// flight rather than the document.
///
/// Entries are scoped by a settings fingerprint. Resuming a run at a different resolution, or on a
/// different execution provider, must not silently reuse results produced under the old settings —
/// the word boxes would be in the wrong coordinate space, and every one of them would be wrong in a
/// way nothing downstream could detect.
/// </summary>
public interface IPageOcrCache
{
    /// <summary>
    /// Returns what recognition produced for a page under these settings, or null if nothing is
    /// cached.
    /// </summary>
    CachedPage? TryGet(string documentPath, int pageNumber, string settingsFingerprint);

    /// <summary>Stores what recognition produced for a page.</summary>
    void Save(string documentPath, int pageNumber, string settingsFingerprint, CachedPage page);

    /// <summary>
    /// Discards everything cached for a document. Called once it is finished, so the cache never
    /// grows beyond the documents actually in flight.
    /// </summary>
    void Clear(string documentPath);
}

/// <summary>
/// One page's recognition, as cached.
///
/// The pixel dimensions are held alongside the words because they are what turns a word box in
/// image pixels into a position on the page, and because without them a reused page still has to be
/// rasterised just to ask how big it was. Rasterising is cheap next to recognition but not free —
/// 55 ms a page measured over 40,000 pages — and a resumed run that skips recognition should not
/// still pay it.
///
/// A row cached before dimensions were stored reports them as zero, which callers read as "not
/// known" and fall back to rasterising. Nothing has to be discarded.
/// </summary>
public sealed record CachedPage(int PixelWidth, int PixelHeight, IReadOnlyList<RecognisedWord> Words)
{
    public bool HasDimensions => PixelWidth > 0 && PixelHeight > 0;
}

/// <summary>A cache that remembers nothing, for callers that do not want resume.</summary>
public sealed class NullPageOcrCache : IPageOcrCache
{
    public static readonly NullPageOcrCache Instance = new();

    public CachedPage? TryGet(string documentPath, int pageNumber, string settingsFingerprint) => null;

    public void Save(string documentPath, int pageNumber, string settingsFingerprint, CachedPage page) { }

    public void Clear(string documentPath) { }
}
