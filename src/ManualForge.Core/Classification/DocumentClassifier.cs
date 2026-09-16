using UglyToad.PdfPig;

namespace ManualForge.Core.Classification;

public enum TextClass
{
    /// <summary>No usable text layer. OCR is pure gain; there is nothing to lose.</summary>
    ImageOnly,

    /// <summary>Text is present but looks garbled. A candidate for strip-and-redo.</summary>
    SuspectText,

    /// <summary>Text is present and mostly clean, but not clean enough to be certain.</summary>
    ProbablyGood,

    /// <summary>Text is present and clean. Leave it alone.</summary>
    GoodText,

    /// <summary>The file could not be read at all.</summary>
    Unreadable,
}

/// <summary>What to do with a class of file.</summary>
public enum ClassAction
{
    /// <summary>Leave the file untouched.</summary>
    Skip,

    /// <summary>OCR and add a text layer.</summary>
    Ocr,

    /// <summary>Remove the existing text layer, then OCR.</summary>
    StripAndRedo,

    /// <summary>
    /// Byte-for-byte identical to another file that is being processed. Take that file's finished
    /// result rather than recognising the same pages twice.
    /// </summary>
    CopyFromDuplicate,
}

public sealed class ClassifierOptions
{
    /// <summary>Pages to sample per document. Sampling keeps a 700-file survey to under a minute.</summary>
    public int SamplePages { get; init; } = 8;

    /// <summary>Below this many alphanumeric characters per page, treat the document as image-only.</summary>
    public double ImageOnlyAlphanumericPerPage { get; init; } = 100;

    /// <summary>At or above this plausible-token ratio, text is clean enough to trust on its own.</summary>
    public double GoodPlausibleRatio { get; init; } = 0.85;

    /// <summary>Below this plausible-token ratio, text is garbled.</summary>
    public double SuspectPlausibleRatio { get; init; } = 0.60;

    /// <summary>
    /// Common-word share that promotes a middling plausible ratio to good. Used only to *promote*,
    /// never to condemn.
    /// </summary>
    public double ProseConfirmingCommonWordShare { get; init; } = 0.15;
}

public sealed record DocumentClassification(
    string Path,
    int PageCount,
    int SampledPages,
    double AlphanumericPerPage,
    double PlausibleTokenRatio,
    double CommonWordShare,
    TextClass Class,
    string Rationale,
    IReadOnlyList<PageTextMetrics> Pages,
    string? Error = null)
{
    public bool HasExistingText => Class is TextClass.SuspectText or TextClass.ProbablyGood or TextClass.GoodText;
}

/// <summary>
/// Decides whether a document's existing text layer is worth keeping.
///
/// Two independent measurements drive this, and conflating them is the trap:
///
/// * <see cref="PageTextMetrics.PlausibleTokenRatio"/> detects bad OCR. Garbled recognition
///   produces tokens littered with stray punctuation, and the ratio collapses.
/// * <see cref="PageTextMetrics.CommonWordShare"/> detects English prose. A parts cross-reference,
///   a multilingual manual or a page of SCPI mnemonics scores near zero with flawless text.
///
/// So a low common-word share never on its own marks a document as suspect. On this corpus that
/// rule matters: `hp_xref-free.pdf` scores 0.99 plausible against 0.04 common words because it is
/// a parts cross-reference, and `U1253BUser.pdf` scores 1.00 against 0.08 because it is
/// multilingual. Both have perfect text layers.
///
/// The numbers are always reported alongside the verdict so the decision can be overridden per
/// class rather than taken on trust.
/// </summary>
public sealed class DocumentClassifier(ClassifierOptions? options = null)
{
    private readonly ClassifierOptions _options = options ?? new ClassifierOptions();

    public ClassifierOptions Options => _options;

    public DocumentClassification Classify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });
            var pageCount = document.NumberOfPages;
            var metrics = new List<PageTextMetrics>();

            foreach (var pageNumber in SamplePageNumbers(pageCount, _options.SamplePages))
            {
                try
                {
                    // GetWords, not Text: see the remarks on TextMetricsCalculator.
                    var words = document.GetPage(pageNumber).GetWords().Select(w => w.Text);
                    metrics.Add(TextMetricsCalculator.Measure(pageNumber, words));
                }
                catch (Exception)
                {
                    // One unreadable page should not condemn a 400-page manual.
                    metrics.Add(PageTextMetrics.Empty(pageNumber));
                }
            }

            return Summarise(path, pageCount, metrics);
        }
        catch (Exception ex)
        {
            return new DocumentClassification(
                path, 0, 0, 0, 0, 0, TextClass.Unreadable,
                "The file could not be opened.", [], ex.Message);
        }
    }

    public DocumentClassification Summarise(string path, int pageCount, IReadOnlyList<PageTextMetrics> metrics)
    {
        if (metrics.Count == 0)
        {
            return new DocumentClassification(
                path, pageCount, 0, 0, 0, 0, TextClass.Unreadable,
                "No pages could be sampled.", metrics);
        }

        var alphanumericPerPage = metrics.Average(m => (double)m.AlphanumericCount);

        // Pool the counts rather than averaging the per-page ratios: a document with one dense page
        // and seven sparse ones should be judged on the text it actually has.
        var tokens = metrics.Sum(m => (long)m.TokenCount);
        var plausibleTokens = metrics.Sum(m => (long)m.PlausibleTokenCount);
        var wordLike = metrics.Sum(m => (long)m.WordLikeTokenCount);
        var commonWords = metrics.Sum(m => (long)m.CommonWordCount);

        var plausibleRatio = tokens == 0 ? 0 : plausibleTokens / (double)tokens;
        var commonShare = wordLike == 0 ? 0 : commonWords / (double)wordLike;

        var (textClass, rationale) = Decide(alphanumericPerPage, plausibleRatio, commonShare);

        return new DocumentClassification(
            path, pageCount, metrics.Count,
            alphanumericPerPage, plausibleRatio, commonShare,
            textClass, rationale, metrics);
    }

    private (TextClass Class, string Rationale) Decide(
        double alphanumericPerPage, double plausibleRatio, double commonShare)
    {
        if (alphanumericPerPage < _options.ImageOnlyAlphanumericPerPage)
        {
            return (TextClass.ImageOnly,
                $"{alphanumericPerPage:F0} characters per page, below the {_options.ImageOnlyAlphanumericPerPage:F0} " +
                "threshold, so there is effectively no text layer.");
        }

        if (plausibleRatio >= _options.GoodPlausibleRatio)
        {
            var prose = commonShare >= _options.ProseConfirmingCommonWordShare
                ? "and reads as prose"
                : "though it is not prose — part numbers, tables or another language, which is not a fault";
            return (TextClass.GoodText,
                $"{plausibleRatio:P0} of tokens are well formed {prose} ({commonShare:P0} common words).");
        }

        if (plausibleRatio < _options.SuspectPlausibleRatio)
        {
            return (TextClass.SuspectText,
                $"only {plausibleRatio:P0} of tokens are well formed, below the " +
                $"{_options.SuspectPlausibleRatio:P0} threshold — the text layer looks garbled.");
        }

        // The middle band. Prose is allowed to promote, never to condemn.
        if (commonShare >= _options.ProseConfirmingCommonWordShare)
        {
            return (TextClass.GoodText,
                $"{plausibleRatio:P0} of tokens are well formed and {commonShare:P0} are common words, " +
                "so it reads as genuine prose despite the middling token score.");
        }

        return (TextClass.ProbablyGood,
            $"{plausibleRatio:P0} of tokens are well formed with {commonShare:P0} common words — " +
            "clean enough to be doubtful about, not bad enough to condemn.");
    }

    /// <summary>
    /// Page numbers to sample, spread through the document. The first and last tenth are skipped:
    /// covers, blank versos and end matter are not representative of the body.
    /// </summary>
    public static IReadOnlyList<int> SamplePageNumbers(int pageCount, int sampleSize)
    {
        if (pageCount <= 0 || sampleSize <= 0)
            return [];

        if (pageCount <= sampleSize)
            return Enumerable.Range(1, pageCount).ToArray();

        var low = Math.Max(1, (int)(pageCount * 0.1));
        var high = Math.Min(pageCount, (int)(pageCount * 0.9));
        var span = Math.Max(1, high - low);
        var step = Math.Max(1, span / sampleSize);

        var pages = new List<int>(sampleSize);
        for (var page = low; page <= high && pages.Count < sampleSize; page += step)
            pages.Add(page);

        return pages;
    }
}

/// <summary>Maps each class to what should happen to files in it.</summary>
public sealed class ClassificationPolicy
{
    private readonly Dictionary<TextClass, ClassAction> _actions;

    public ClassificationPolicy(IDictionary<TextClass, ClassAction>? actions = null)
    {
        // The safe default, and the one chosen for the first full run: OCR only what has no text
        // at all, where there is nothing to lose, and leave every existing text layer alone.
        _actions = new Dictionary<TextClass, ClassAction>
        {
            [TextClass.ImageOnly] = ClassAction.Ocr,
            [TextClass.SuspectText] = ClassAction.Skip,
            [TextClass.ProbablyGood] = ClassAction.Skip,
            [TextClass.GoodText] = ClassAction.Skip,
            [TextClass.Unreadable] = ClassAction.Skip,
        };

        if (actions is null)
            return;

        foreach (var (textClass, action) in actions)
            _actions[textClass] = action;
    }

    public ClassAction ActionFor(TextClass textClass) => _actions.GetValueOrDefault(textClass, ClassAction.Skip);

    public IReadOnlyDictionary<TextClass, ClassAction> Actions => _actions;

    /// <summary>Parses <c>ImageOnly=ocr,SuspectText=redo</c> into a policy.</summary>
    public static ClassificationPolicy Parse(string? specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
            return new ClassificationPolicy();

        var actions = new Dictionary<TextClass, ClassAction>();
        foreach (var part in specification.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                throw new ArgumentException($"Expected 'Class=action' in the policy, got '{part}'.", nameof(specification));

            var className = part[..equals].Trim();
            var actionName = part[(equals + 1)..].Trim();

            if (!Enum.TryParse<TextClass>(className, ignoreCase: true, out var textClass))
                throw new ArgumentException($"'{className}' is not a class. Use one of: {string.Join(", ", Enum.GetNames<TextClass>())}.", nameof(specification));

            var action = actionName.ToLowerInvariant() switch
            {
                "ocr" => ClassAction.Ocr,
                "skip" => ClassAction.Skip,
                "redo" or "stripandredo" or "strip" => ClassAction.StripAndRedo,
                "copy" or "copyfromduplicate" => ClassAction.CopyFromDuplicate,
                _ => throw new ArgumentException($"'{actionName}' is not an action. Use ocr, skip or redo.", nameof(specification)),
            };

            actions[textClass] = action;
        }

        return new ClassificationPolicy(actions);
    }
}
