using ManualForge.Core.Auditing;
using ManualForge.Core.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManualForge.Shell;

/// <summary>
/// One document as the doctor tab shows it, with the findings already read out of the store.
/// </summary>
public sealed record DoctorFinding(
    string Path,
    string Title,
    int PageCount,
    int FlaggedPages,
    int DrawnPages,
    int RepairedPages,
    int RecoverableCharacters,
    int SuggestedDpi,
    DocumentVerdict Verdict,
    string FlaggedPageRanges)
{
    public int OutstandingPages => Math.Max(0, FlaggedPages - RepairedPages);

    public bool IsRepaired => FlaggedPages > 0 && OutstandingPages == 0;
}

/// <summary>What the doctor tab knows before it has done anything: the audit as it stands.</summary>
public sealed record DoctorStanding(
    AuditSummary Summary,
    IReadOnlyList<DoctorFinding> Findings)
{
    public static readonly DoctorStanding None =
        new(new AuditSummary(0, 0, 0, 0, 0), []);

    public bool HasAudit => Summary.DocumentsAudited > 0;
}

/// <summary>
/// Everything the doctor tab needs, behind one seam.
///
/// <para>
/// It exists for the same reason <see cref="ISearchService"/> does: so the view model can be driven
/// by a test with no PDF, no GPU and no OCR model. The repair is the only part that needs any of
/// those, and it is the part whose cancel-and-resume behaviour most wants asserting rather than
/// clicking through.
/// </para>
/// </summary>
public interface IDoctorService
{
    /// <summary>Whether an audit has ever been run over this library.</summary>
    bool HasAudit(string root);

    /// <summary>
    /// Reads what an earlier audit found. Cheap — it opens a database and nothing else — which is
    /// what lets the tab be useful over a corpus whose audit was run from the command line.
    /// </summary>
    Task<DoctorStanding> ReadAsync(string root, CancellationToken cancellationToken);

    /// <summary>Audits the library, changing nothing but the findings database.</summary>
    Task<DoctorStanding> AuditAsync(
        string root,
        DoctorOptions options,
        IProgress<DoctorProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Recovers the text on flagged pages. The expensive one: it loads the OCR models and uses the
    /// GPU, and on a large library it is the part somebody sits and watches.
    /// </summary>
    Task<RepairReport> RepairAsync(
        string root,
        IReadOnlyList<string> paths,
        RepairOptions options,
        IProgress<RepairProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// The picture the audit worked from for one page, as PNG bytes: grey for ink an extracted
    /// glyph accounts for, black for ink nothing accounts for, boxes round what was counted as
    /// lettering. Null when the page cannot be rendered.
    /// </summary>
    Task<byte[]?> ExplainAsync(string path, int pageNumber, CancellationToken cancellationToken);
}

/// <summary>
/// The real one: <see cref="DoctorRunner"/>, <see cref="DoctorStore"/> and
/// <see cref="PageRepairer"/> with the threading and the disposal tidied up.
/// </summary>
public sealed class DoctorService(
    ILoggerFactory? loggerFactory = null,
    OcrEngineOptions? engineOptions = null) : IDoctorService
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    /// <summary>
    /// Deskew and despeckle off, deliberately: the pages being repaired are rendered from vector
    /// drawing instructions or from a scan the recogniser has already straightened once, and
    /// despeckling erodes the 4 pt annotation on a syntax diagram, which is the text being
    /// recovered. The command line makes the same choice for the same reason.
    /// </summary>
    private readonly OcrEngineOptions _engineOptions = engineOptions ?? new OcrEngineOptions
    {
        Deskew = false,
        Denoise = false,
    };

    public bool HasAudit(string root) => File.Exists(DoctorStore.DefaultPathFor(root));

    public Task<DoctorStanding> ReadAsync(string root, CancellationToken cancellationToken) =>
        Task.Run(() => Read(root), cancellationToken);

    public async Task<DoctorStanding> AuditAsync(
        string root,
        DoctorOptions options,
        IProgress<DoctorProgress>? progress,
        CancellationToken cancellationToken)
    {
        await new DoctorRunner(_loggerFactory.CreateLogger<DoctorRunner>())
            .RunAsync(root, options, storePath: null, force: false, progress, cancellationToken)
            .ConfigureAwait(false);

        return Read(root);
    }

    public async Task<RepairReport> RepairAsync(
        string root,
        IReadOnlyList<string> paths,
        RepairOptions options,
        IProgress<RepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // The engine is built per run rather than held. It pins several hundred megabytes of VRAM,
        // and an application that sits idle all afternoon holding a card the rest of the machine
        // wants is worse company than one that takes a few seconds to start a repair.
        await using var engine = new PaddleOcrEngine(
            _engineOptions, _loggerFactory.CreateLogger<PaddleOcrEngine>());

        using var store = new DoctorStore(DoctorStore.DefaultPathFor(root));

        var repairer = new PageRepairer(engine, _loggerFactory.CreateLogger<PageRepairer>());

        return await repairer
            .RepairAsync(store, options with { Paths = paths }, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<byte[]?> ExplainAsync(string path, int pageNumber, CancellationToken cancellationToken) =>
        Task.Run<byte[]?>(
            () =>
            {
                var (_, diagnostic) = new UnderExtractionDetector().Explain(path, pageNumber);
                if (diagnostic is null)
                    return null;

                using (diagnostic)
                using (var data = diagnostic.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                {
                    return data.ToArray();
                }
            },
            cancellationToken);

    private static DoctorStanding Read(string root)
    {
        var databasePath = DoctorStore.DefaultPathFor(root);
        if (!File.Exists(databasePath))
            return DoctorStanding.None;

        using var store = new DoctorStore(databasePath, readOnly: true);

        var findings = store.Flagged()
            .Select(d => new DoctorFinding(
                d.Path,
                d.Title,
                d.PageCount,
                d.FlaggedPages,
                d.DrawnPages,
                d.RepairedPages,
                d.RecoverableCharacters,
                d.SuggestedDpi,
                d.Verdict,
                PageRanges.Format(
                    store.Findings(d.Path, PageVerdict.UnderExtracted).Select(f => f.PageNumber))))
            .ToArray();

        return new DoctorStanding(store.Summary(), findings);
    }
}
