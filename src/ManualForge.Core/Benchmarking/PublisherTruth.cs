using ManualForge.Core.Auditing;
using ManualForge.Core.Indexing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualForge.Core.Benchmarking;

/// <summary>One page whose own text layer can stand as truth, and the text it holds.</summary>
public sealed record PublisherPage(string ManualPath, int PageNumber, PageKind Kind, string Text)
{
    /// <summary>Characters the page's text layer decodes to. The size of the yardstick.</summary>
    public int Characters => Text.Length;
}

/// <summary>
/// Finds pages whose text layer is the publisher's own typesetting, so that a benchmark can be run
/// without anybody transcribing anything.
///
/// <para>
/// A born-digital page already carries a correct transcription of itself: the characters in the
/// content stream are what the typesetter put there, not a machine's reading of a picture. Render
/// such a page, recognise the render, and the text layer is the answer key.
/// </para>
///
/// <para>
/// <b>This is not a substitute for hand-corrected scans and must never be quoted as one.</b> A
/// clean render of digital type is an easier thing to read than a 1965 photocopy with halftone
/// screening, bleed-through and a skew from the book's spine. What it measures honestly is the
/// recogniser's floor, and — because the page's glyph positions are exact — anything geometric,
/// which is what <c>TextLayerOptions.BaselineOffsetFraction</c> needs. What it cannot measure is
/// deskew and denoise, since there is nothing here to straighten or clean.
/// </para>
/// </summary>
public sealed class PublisherTruth(ILogger<PublisherTruth>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>
    /// The share of a page's drawn glyphs that must decode to characters.
    ///
    /// <para>
    /// Deliberately not near 1.0, which was the first thing tried and disqualified the entire
    /// library. A clean, modern, born-digital page routinely decodes about seven glyphs in ten:
    /// spacing glyphs, ligatures and kerning artefacts are drawn and carry no character, and a
    /// subset font with no <c>/ToUnicode</c> still extracts correctly through its standard
    /// encoding. What a low ratio really means is text converted to outlines, and 0.5 sits below
    /// anything healthy and well above that.
    /// </para>
    /// <para>
    /// The guarantee that a page's text is *complete* does not come from this number at all. It
    /// comes from the ink comparison — see <see cref="Confirm"/>.
    /// </para>
    /// </summary>
    public double MinimumDecodedShare { get; init; } = 0.5;

    /// <summary>
    /// Characters a page must decode to before it is worth scoring. Below this the page is mostly
    /// figure, and a handful of labels makes for a noisy error rate.
    /// </summary>
    public int MinimumCharacters { get; init; } = 900;

    /// <summary>
    /// How much of the page an image may cover.
    ///
    /// <para>
    /// The important criterion, and the one that decides whether this whole idea works. A scanned
    /// page carrying 2000s-era OCR *also* has a text layer, and using it as truth would score this
    /// recogniser against another engine's mistakes while calling the result accuracy. Near zero
    /// image coverage is what separates type from a photograph of type.
    /// </para>
    /// </summary>
    public double MaximumImageCoverage { get; init; } = 0.02;

    /// <summary>At most this many pages from any one document, so one manual cannot become the set.</summary>
    public int PagesPerDocument { get; init; } = 2;

    /// <summary>
    /// The share of a page's words that may be a single stray letter before its text is rejected as
    /// mangled.
    ///
    /// <para>
    /// Extraction is not transcription. On a multi-column page with tight tracking, words come back
    /// letter-spaced and columns interleaved — <c>Co nt inu e d fro m fro nt m a tte r</c> is a real
    /// example from this library, from a page that passes every other test here. Scoring a
    /// recogniser against that would mark it wrong for reading the page correctly, which is worse
    /// than having no benchmark at all.
    /// </para>
    /// <para>
    /// 3% of tokens, ignoring <c>a</c> and <c>I</c> which are words. Clean prose sits near zero;
    /// the mangled page above is far above it.
    /// </para>
    /// </summary>
    public double MaximumStrayLetterShare { get; init; } = 0.03;

    /// <summary>
    /// The mean length of a page's words, below which its text is taken to be broken up rather than
    /// written. English technical prose runs about 5; letter-spaced extraction collapses it.
    /// </summary>
    public double MinimumMeanWordLength { get; init; } = 3.5;

    /// <summary>
    /// The share of a page's two- and three-letter words that may be neither an ordinary short word
    /// nor a unit before its text is rejected.
    ///
    /// <para>
    /// The single-letter test above misses the commonest form of the damage. <c>Vi sual User Int
    /// erface</c> is a real extraction from this library, and every fragment in it is two or three
    /// letters long. In prose that reads properly, nearly every short word is one of a few dozen —
    /// <c>the</c>, <c>and</c>, <c>for</c>, <c>of</c> — or a unit like <c>dB</c> or <c>Hz</c>.
    /// Fragments are not.
    /// </para>
    /// </summary>
    public double MaximumOddShortWordShare { get; init; } = 0.35;

    /// <summary>
    /// Short words that belong in technical prose, against which the test above is made. Not a
    /// lexicon and not meant to be one: it only has to cover what is common enough that its absence
    /// says the text is broken.
    /// </summary>
    private static readonly HashSet<string> OrdinaryShortWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "can", "has", "was", "its", "may",
        "use", "one", "two", "six", "ten", "see", "per", "set", "low", "off", "out", "via", "any",
        "new", "old", "end", "top", "add", "run", "key", "way", "get", "put", "let", "how", "why",
        "is", "in", "on", "of", "to", "at", "by", "be", "as", "an", "or", "if", "it", "we", "no",
        "so", "up", "do", "he", "me", "my", "us", "a",
        "dc", "ac", "hz", "db", "khz", "mhz", "ghz", "ms", "us", "ns", "ps", "mv", "uv", "kv",
        "ma", "ua", "na", "pa", "mw", "kw", "vp", "pp", "rf", "if", "lo", "cw", "am", "fm", "pm",
        "id", "od", "cm", "mm", "nm", "kg", "lb", "in", "ft", "pc", "io", "os", "cd", "rom", "ram",
        "cpu", "bit", "lsb", "msb", "ttl", "cmo", "pcb", "ic", "led", "lcd", "bnc", "ohm", "vac",
        "vdc", "rms", "max", "min", "avg", "ref", "cal", "adj", "amp", "osc", "sec", "hrs", "deg",
    };

    /// <summary>
    /// Path-painting operations a page may make and still be taken as truth.
    ///
    /// <para>
    /// A page carrying a vector figure is disqualified even though its text decodes perfectly,
    /// because the figure's labels are *drawn* rather than set: the recogniser will read them off
    /// the render and be marked wrong for text the truth does not contain. 40 is the same threshold
    /// the audit uses to decide a page is worth rendering, and for the same reason — below it there
    /// is page furniture, above it there is a drawing.
    /// </para>
    /// </summary>
    public int MaximumPathOperations { get; init; } = 40;

    /// <summary>
    /// Stage one of the audit with the render gate wired shut, which is all this needs.
    ///
    /// <para>
    /// Everything the criteria below ask about — glyphs drawn, characters decoded, image coverage,
    /// path operations, the fonts — is read from the content stream. The ink comparison is what
    /// costs a render, and it answers a question about pages that are *missing* text rather than
    /// pages that are complete. Leaving it out turns minutes per document into milliseconds.
    /// </para>
    /// </summary>
    private static DoctorOptions WithoutRendering() => new()
    {
        RenderBelowCharactersPerPage = 0,
        RenderAtOrAbovePathOperations = int.MaxValue,
        RenderAtOrAboveImageCoverage = 2.0,
    };

    /// <summary>
    /// Walks a library and returns pages fit to be truth, newest-seeded-random order, at most
    /// <paramref name="count"/> of them.
    /// </summary>
    public IReadOnlyList<PublisherPage> Select(
        string libraryRoot,
        int count,
        int seed = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);

        var random = new Random(seed);
        var documents = Directory
            .EnumerateFiles(libraryRoot, "*.pdf", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}_Originals{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => new FileInfo(path).Length > 0)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(_ => random.Next())
            .ToArray();

        var chosen = new List<PublisherPage>();
        var detector = new UnderExtractionDetector(WithoutRendering());

        // Scanning houses put a born-digital notice page at the front of every manual they sell, so
        // the same page qualifies once per document and a set can fill up with twelve copies of it.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (chosen.Count >= count)
                break;

            IReadOnlyList<PageAudit> audited;

            try
            {
                audited = detector.Audit(document, cancellationToken: cancellationToken).Pages;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Skipping {Path}", document);
                continue;
            }

            // Most of this library is scans, and a scan qualifies for nothing here. Deciding that
            // from the parse alone, before extracting a word of text, is what keeps this quick.
            if (!audited.Any(IsPublisherType))
                continue;

            if (!WasTypeset(document))
            {
                _logger.LogDebug("{Path} looks scanned rather than typeset", Path.GetFileName(document));
                continue;
            }

            IReadOnlyList<IndexedPageText> text;

            try
            {
                text = LibraryIndexer.ExtractPages(document, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Skipping {Path}", document);
                continue;
            }

            var taken = 0;

            foreach (var page in audited)
            {
                if (chosen.Count >= count || taken >= PagesPerDocument)
                    break;

                if (!IsPublisherType(page))
                    continue;

                var index = page.PageNumber - 1;
                if (index < 0 || index >= text.Count)
                    continue;

                var pageText = text[index].Text;
                if (pageText.Length < MinimumCharacters)
                    continue;

                if (!ReadsAsWritten(pageText))
                    continue;

                if (!seen.Add(Fingerprint(pageText)))
                    continue;

                if (!Confirm(document, page.PageNumber))
                    continue;

                chosen.Add(new PublisherPage(document, page.PageNumber, KindOf(pageText), pageText));
                taken++;
            }

            if (taken > 0)
                _logger.LogInformation("{Path}: took {Taken} page(s)", Path.GetFileName(document), taken);
        }

        return chosen;
    }

    /// <summary>
    /// Whether a document was typeset rather than scanned, judged on whether its pages are all the
    /// same size.
    ///
    /// <para>
    /// The trap this closes is the one that nearly spoiled the whole idea. Some manuals here are
    /// scans that were OCR'd and then had their page images dropped, leaving a text-only document
    /// that passes every per-page test above — no image, text decodes, nothing unaccounted for —
    /// while its "text" is another engine's reading, complete with mistakes. One of them yielded
    /// <c>Section Ill. TECHNICAL PRINCIPLES OF OPERATION</c>, and scoring against that would have
    /// marked this recogniser wrong for reading <c>III</c> correctly.
    /// </para>
    /// <para>
    /// A scanner's crop wanders by a point or two from page to page: 614x797, 610x792, 613x794. A
    /// typesetter's does not. So: at least nine pages in ten must share one size to within a point,
    /// landscape pages excepted, since a fold-out is legitimate.
    /// </para>
    /// </summary>
    public static bool WasTypeset(string path)
    {
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(
                path, new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true });

            var sizes = new List<(int Width, int Height)>();
            foreach (var page in document.GetPages())
            {
                var width = (int)Math.Round(page.Width);
                var height = (int)Math.Round(page.Height);

                if (width > height)
                    continue;

                sizes.Add((width, height));
                if (sizes.Count >= 60)
                    break;
            }

            if (sizes.Count < 3)
                return false;

            var commonest = sizes
                .GroupBy(size => size)
                .OrderByDescending(group => group.Count())
                .First();

            var matching = sizes.Count(size =>
                Math.Abs(size.Width - commonest.Key.Width) <= 1
                && Math.Abs(size.Height - commonest.Key.Height) <= 1);

            return matching >= sizes.Count * 0.9;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a page's extracted text reads like written language rather than like the output of
    /// an extractor that lost the layout.
    ///
    /// <para>
    /// The most important quality gate here, and the least obvious. Every other test asks whether
    /// the page's text is *present*; this asks whether it is *right*. Text that comes back
    /// letter-spaced, or with two columns interleaved line by line, is present, complete, and
    /// useless as an answer key.
    /// </para>
    /// </summary>
    public bool ReadsAsWritten(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 50)
            return false;

        var strays = words.Count(word =>
            word.Length == 1 && char.IsLetter(word[0]) && word is not ("a" or "A" or "I"));

        if (strays > words.Length * MaximumStrayLetterShare)
            return false;

        if (words.Average(word => (double)word.Length) < MinimumMeanWordLength)
            return false;

        var shortWords = words
            .Select(word => word.Trim('.', ',', ':', ';', '(', ')', '"', '\'', '-'))
            .Where(word => word.Length is 2 or 3 && word.All(char.IsLetter))
            .ToArray();

        if (shortWords.Length < 10)
            return true;

        var odd = shortWords.Count(word => !OrdinaryShortWords.Contains(word));
        return odd <= shortWords.Length * MaximumOddShortWordShare;
    }

    /// <summary>
    /// Identifies a page by its words, so that the same boilerplate under two file names is
    /// recognised as one page. Deliberately crude: lower-cased letters and digits only, first 400
    /// characters, which is enough to catch a reprinted notice and not enough to collide.
    /// </summary>
    private static string Fingerprint(string text)
    {
        var letters = text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).Take(400).ToArray();
        return new string(letters);
    }

    /// <summary>
    /// Renders one shortlisted page and asks whether any ink on it is unaccounted for.
    ///
    /// <para>
    /// This is the criterion that matters and the only expensive one, so it runs on a handful of
    /// candidates rather than on a library. Everything before it is a filter over the content
    /// stream; this is the measurement, and it answers the question truth actually depends on —
    /// <i>is there anything on this page that the text layer does not contain?</i> A page that
    /// passes carries no lettering the recogniser could read and be marked wrong for.
    /// </para>
    /// </summary>
    public bool Confirm(string path, int pageNumber)
    {
        try
        {
            var (audit, diagnostic) = new UnderExtractionDetector().Explain(path, pageNumber);
            diagnostic?.Dispose();
            return audit.Verdict == PageVerdict.Fine;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not confirm {Path} page {Page}", path, pageNumber);
            return false;
        }
    }

    /// <summary>Whether this page's text layer is the typesetter's own, and complete.</summary>
    public bool IsPublisherType(PageAudit page)
    {
        ArgumentNullException.ThrowIfNull(page);

        // Fine is the only acceptable verdict, and with the gate wired shut it is also what a page
        // gets when nothing disqualified it earlier: no text layer, or glyphs that do not decode,
        // are decided before any rendering and keep their own verdicts.
        if (page.Verdict != PageVerdict.Fine)
            return false;

        // Type 3 fonts are bitmap or procedure glyphs and extract unreliably. A missing /ToUnicode
        // is *not* disqualifying: it fires on most of this library's born-digital pages, which
        // extract perfectly through their standard encodings, and excluding them leaves nothing.
        if (page.HasType3Font)
            return false;

        if (page.PathPaintOperations >= MaximumPathOperations)
            return false;

        if (page.ImageCoverage > MaximumImageCoverage)
            return false;

        if (page.GlyphsDrawn <= 0 || page.CharactersDecoded < MinimumCharacters)
            return false;

        return page.CharactersDecoded >= page.GlyphsDrawn * MinimumDecodedShare;
    }

    /// <summary>
    /// Prose or table, decided on shape.
    ///
    /// <para>
    /// The distinction earns its place: an engine that reads running text beautifully can still
    /// make a mess of a parts list, and one average hides that. A page is called a table when a
    /// good share of its lines are short and carry digits — part numbers, values, units — which is
    /// what a specification table looks like from the outside. Schematics are not offered: a page
    /// of vector line art has too few characters to qualify here at all.
    /// </para>
    /// </summary>
    public static PageKind KindOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        if (lines.Length == 0)
            return PageKind.Mixed;

        var tabular = lines.Count(line =>
            line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6
            && line.Any(char.IsDigit));

        return tabular >= lines.Length * 0.4 ? PageKind.Table : PageKind.Prose;
    }
}
