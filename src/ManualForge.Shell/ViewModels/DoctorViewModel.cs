using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManualForge.Core.Auditing;

namespace ManualForge.Shell.ViewModels;

/// <summary>What the doctor tab is doing, which decides what can be pressed.</summary>
public enum DoctorState
{
    Idle,
    Reading,
    Auditing,
    Repairing,
    Cancelling,
}

/// <summary>
/// One document in the doctor tab's list, with the tick box that decides whether it gets repaired.
/// </summary>
public sealed partial class DoctorRow(DoctorFinding finding) : ObservableObject
{
    public DoctorFinding Finding { get; private set; } = finding;

    /// <summary>
    /// Whether this document is included in the next repair. Drawn documents start ticked because
    /// they are the finding; a scanned document with OCR gaps does not, because repairing all of
    /// those is hours of work and should be an explicit choice.
    /// </summary>
    [ObservableProperty]
    private bool _selected = finding.Verdict is DocumentVerdict.UnderExtracted or DocumentVerdict.Figures
                             && finding.OutstandingPages > 0;

    public string Path => Finding.Path;

    public string Title => Finding.Title;

    /// <summary>What a screen reader calls this row's checkbox, which has no visible label of its own.</summary>
    public string TickLabel => $"Recover text for {Title}";

    public int PageCount => Finding.PageCount;

    public int FlaggedPages => Finding.FlaggedPages;

    public int DrawnPages => Finding.DrawnPages;

    public int OutstandingPages => Finding.OutstandingPages;

    public int RecoverableCharacters => Finding.RecoverableCharacters;

    public string PageRanges => Finding.FlaggedPageRanges;

    public string Kind => Finding.Verdict switch
    {
        DocumentVerdict.UnderExtracted => "drawn, never text",
        DocumentVerdict.ScannedGaps => "scanned, OCR gaps",
        DocumentVerdict.Figures => "figure pages",
        _ => "sound",
    };

    public string Share => PageCount == 0
        ? "-"
        : ((double)FlaggedPages / PageCount).ToString("P0", CultureInfo.CurrentCulture);

    public string Progress => Finding.FlaggedPages == 0
        ? "-"
        : $"{Finding.RepairedPages:N0}/{Finding.FlaggedPages:N0}";

    public bool IsRepaired => Finding.IsRepaired;

    /// <summary>Replaces the finding after a repair, keeping the row and its tick box in place.</summary>
    public void Update(DoctorFinding finding)
    {
        Finding = finding;

        OnPropertyChanged(nameof(Finding));
        OnPropertyChanged(nameof(OutstandingPages));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsRepaired));

        if (finding.OutstandingPages == 0)
            Selected = false;
    }
}

/// <summary>
/// The doctor tab: what the audit found, which documents to recover the text from, and the picture
/// the audit worked from for any page somebody doubts.
///
/// <para>
/// The audit itself is cheap to look at and expensive to run — over a hundred thousand pages it is
/// a couple of hours — so the tab reads the findings the command line wrote rather than insisting
/// on producing them itself. Running one from here is offered for a single folder, where it takes
/// seconds.
/// </para>
///
/// <para>
/// The repair is the other way round: it is the part that wants a person watching it, choosing
/// which manual is worth twenty minutes of GPU time, seeing the words come back and being able to
/// stop. That is why it is here and not only on the command line.
/// </para>
/// </summary>
public sealed partial class DoctorViewModel(IDoctorService service, IUiDispatcher? dispatcher = null)
    : ObservableObject
{
    private readonly IDoctorService _service = service ?? throw new ArgumentNullException(nameof(service));
    private readonly IUiDispatcher _dispatcher = dispatcher ?? InlineDispatcher.Instance;

    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AuditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    private string? _folder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AuditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(PageProgress))]
    [NotifyPropertyChangedFor(nameof(WorkProgress))]
    [NotifyPropertyChangedFor(nameof(IsWorkIndeterminate))]
    private DoctorState _state = DoctorState.Idle;

    [ObservableProperty]
    private string _status = "Pick a folder, then read what the audit found.";

    [ObservableProperty]
    private string? _headline;

    [ObservableProperty]
    private bool _hasAudit;

    /// <summary>
    /// Whether scanned documents whose earlier OCR missed lettering are offered for repair. Off by
    /// default: on a corpus this size that is ten thousand pages against a few hundred, and a
    /// different problem from the one the audit was built to find.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCount))]
    private bool _includeScannedPages;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageProgress))]
    [NotifyPropertyChangedFor(nameof(WorkProgress))]
    [NotifyPropertyChangedFor(nameof(IsWorkIndeterminate))]
    private int _documentsDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageProgress))]
    [NotifyPropertyChangedFor(nameof(WorkProgress))]
    [NotifyPropertyChangedFor(nameof(IsWorkIndeterminate))]
    private int _documentsTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageProgress))]
    [NotifyPropertyChangedFor(nameof(WorkProgress))]
    [NotifyPropertyChangedFor(nameof(IsWorkIndeterminate))]
    private int _pagesDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageProgress))]
    [NotifyPropertyChangedFor(nameof(WorkProgress))]
    [NotifyPropertyChangedFor(nameof(IsWorkIndeterminate))]
    private int _pagesTotal;

    [ObservableProperty]
    private string? _currentFile;

    [ObservableProperty]
    private string? _currentPage;

    /// <summary>
    /// The document whose pages Show draws. Choosing one did not re-check whether Show could run, so it
    /// stayed disabled until the page number was changed as well (#74).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExplainCommand))]
    [NotifyPropertyChangedFor(nameof(ExplainHint))]
    private DoctorRow? _selectedDocument;

    /// <summary>A newly chosen document starts at the first page the audit flagged in it.</summary>
    partial void OnSelectedDocumentChanged(DoctorRow? value)
    {
        Diagnostic = null;
        PageExplanation = null;
        if (value is not null)
            ExplainPage = FirstPageOf(value.PageRanges) ?? 1;
    }

    /// <summary>The first page of a list such as "14,16,21-26", or null if it has none.</summary>
    public static int? FirstPageOf(string? ranges)
    {
        var digits = new string((ranges ?? "").TrimStart().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : null;
    }

    /// <summary>What the empty picture panel says, so it is clear what Show needs and what it draws.</summary>
    public string ExplainHint => SelectedDocument is null
        ? "Click or tick a document in the list to look at the pages the audit flagged in it. Show draws the page: "
          + "grey is text already in the document's text layer, black is everything else drawn on it, and red boxes "
          + "mark lettering the text layer is missing."
        : $"Show draws page {ExplainPage:N0} of {SelectedDocument.Title}. Its flagged pages are listed under "
          + "its name.";

    /// <summary>
    /// The page whose diagnostic picture to show. A number rather than a picker, because what
    /// somebody wants to check is "page 40 of this manual", which is what the report just told them.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExplainCommand))]
    [NotifyPropertyChangedFor(nameof(ExplainHint))]
    private int _explainPage;

    /// <summary>
    /// The picture, as PNG bytes. Bytes rather than an image type so this stays free of any
    /// dependency on a UI framework and can be asserted in a test.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPicture))]
    private byte[]? _diagnostic;

    /// <summary>Whether the picture panel is empty, and shows <see cref="ExplainHint"/> instead.</summary>
    public bool HasNoPicture => Diagnostic is null;

    public ObservableCollection<DoctorRow> Documents { get; } = [];

    public ObservableCollection<string> Recovered { get; } = [];

    public bool IsBusy => State is not DoctorState.Idle;

    public bool IsIdle => State is DoctorState.Idle;

    public double PageProgress => PagesTotal <= 0 ? 0 : 100.0 * PagesDone / PagesTotal;

    /// <summary>
    /// The tab's progress bar, 0 to 100: documents checked during an audit, pages recovered during a
    /// repair. It used to show <see cref="PageProgress"/> alone, which an audit never moves, and with
    /// no change notification it did not move during a repair either (#74).
    /// </summary>
    public double WorkProgress => State switch
    {
        DoctorState.Auditing => DocumentsTotal <= 0 ? 0 : 100.0 * DocumentsDone / DocumentsTotal,
        DoctorState.Repairing => PageProgress,
        _ => 0,
    };

    /// <summary>
    /// Whether the bar should show motion rather than an amount: while the findings are read, and
    /// before an audit or a repair has reported anything (a repair first loads the OCR models).
    /// </summary>
    public bool IsWorkIndeterminate => State switch
    {
        DoctorState.Reading => true,
        DoctorState.Auditing => DocumentsTotal <= 0,
        DoctorState.Repairing => PagesDone <= 0,
        _ => false,
    };

    private bool CanAct => IsIdle && !string.IsNullOrWhiteSpace(Folder);

    private bool CanRepair => CanAct && SelectedCount > 0;

    /// <summary>
    /// What an audit in progress says. A file unchanged since the last audit is only hashed, which on a
    /// library on OneDrive still reads every byte, so it is counted out loud rather than in silence.
    /// </summary>
    public static string AuditStatus(DoctorProgress p)
    {
        var audited = p.DocumentsDone - p.DocumentsUnchanged;
        return $"Checking {p.DocumentsDone:N0} of {p.DocumentsTotal:N0} documents: "
               + $"{p.DocumentsUnchanged:N0} unchanged since the last audit, {audited:N0} audited, "
               + $"{p.FlaggedPages:N0} page(s) flagged so far. Nothing is being changed.";
    }

    /// <summary>Reads what an earlier audit found, without auditing anything.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(Folder))
            return;

        State = DoctorState.Reading;
        Status = "Reading the audit...";

        try
        {
            HasAudit = _service.HasAudit(Folder);
            Show(await _service.ReadAsync(Folder, CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            Status = $"Could not read the audit: {ex.Message}";
        }
        finally
        {
            State = DoctorState.Idle;
        }
    }

    /// <summary>Audits the folder. Changes nothing except the findings database.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task AuditAsync()
    {
        if (string.IsNullOrWhiteSpace(Folder))
            return;

        _cancellation = new CancellationTokenSource();
        State = DoctorState.Auditing;
        Status = "Auditing: finding the documents and checking which have changed since the last audit. Nothing is being changed.";
        DocumentsDone = DocumentsTotal = 0;

        var progress = new DispatchedProgress<DoctorProgress>(_dispatcher, p =>
        {
            DocumentsDone = p.DocumentsDone;
            DocumentsTotal = p.DocumentsTotal;
            CurrentFile = Path.GetFileName(p.Path);
            Status = AuditStatus(p);
        });

        try
        {
            var standing = await _service
                .AuditAsync(Folder, new DoctorOptions(), progress, _cancellation.Token)
                .ConfigureAwait(true);

            HasAudit = true;
            Show(standing);
        }
        catch (OperationCanceledException)
        {
            Status = "Audit cancelled. What it had already found is kept.";
            await ReloadQuietlyAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"The audit failed: {ex.Message}";
        }
        finally
        {
            CurrentFile = null;
            State = DoctorState.Idle;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>
    /// Recovers the text on the flagged pages of the ticked documents. Nothing is written to any
    /// PDF; the text goes into the findings database and reaches search at the next index build.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRepair))]
    public async Task RepairAsync()
    {
        if (string.IsNullOrWhiteSpace(Folder))
            return;

        var chosen = Documents.Where(d => d.Selected).ToArray();
        if (chosen.Length == 0)
            return;

        _cancellation = new CancellationTokenSource();
        State = DoctorState.Repairing;
        Recovered.Clear();
        PagesDone = 0;
        PagesTotal = chosen.Sum(d => d.OutstandingPages);
        Status = $"Recovering text from {PagesTotal:N0} page(s). Loading the OCR models...";

        var progress = new DispatchedProgress<RepairProgress>(_dispatcher, p =>
        {
            PagesDone = p.PagesDone;
            PagesTotal = Math.Max(PagesTotal, p.PagesTotal);
            CurrentFile = Path.GetFileName(p.Path);
            CurrentPage = $"page {p.PageNumber:N0}";
            Status = $"Recovering: {p.PagesDone:N0} of {PagesTotal:N0} page(s).";

            Recovered.Insert(
                0,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"{Path.GetFileName(p.Path)} page {p.PageNumber:N0} — {p.WordsRecovered:N0} words"));

            // The list is a window on what is happening, not a log. The log is the log.
            while (Recovered.Count > 200)
                Recovered.RemoveAt(Recovered.Count - 1);
        });

        try
        {
            var report = await _service
                .RepairAsync(
                    Folder,
                    chosen.Select(d => d.Path).ToArray(),
                    new RepairOptions { IncludeScannedPages = IncludeScannedPages },
                    progress,
                    _cancellation.Token)
                .ConfigureAwait(true);

            await ReloadQuietlyAsync().ConfigureAwait(true);

            Status =
                $"Recovered {report.WordsRecovered:N0} words from {report.PagesRepaired:N0} page(s) " +
                $"across {report.DocumentsRepaired:N0} document(s) at {report.MeanConfidence:P0} mean " +
                "confidence. Nothing was written to any PDF — build the index to make it searchable.";

            if (report.DocumentsStale > 0)
            {
                Status += $" {report.DocumentsStale:N0} document(s) changed since the audit and were " +
                          "skipped; audit them again.";
            }
        }
        catch (OperationCanceledException)
        {
            await ReloadQuietlyAsync().ConfigureAwait(true);
            Status = $"Stopped. The {PagesDone:N0} page(s) already recovered are kept; " +
                     "repairing again picks up where this left off.";
        }
        catch (Exception ex)
        {
            Status = $"The repair failed: {ex.Message}";
        }
        finally
        {
            CurrentFile = null;
            CurrentPage = null;
            State = DoctorState.Idle;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>
    /// Draws what the audit saw on one page. The point of the tab having this at all is that
    /// nobody should have to take a detector's word for it before spending GPU time on what it said.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExplain))]
    public async Task ExplainAsync()
    {
        if (SelectedDocument is null || ExplainPage < 1)
            return;

        Diagnostic = null;
        Status = $"Rendering page {ExplainPage:N0} of {SelectedDocument.Title}...";

        try
        {
            Diagnostic = await _service
                .ExplainAsync(SelectedDocument.Path, ExplainPage, CancellationToken.None)
                .ConfigureAwait(true);

            if (Diagnostic is null)
            {
                Status = $"Page {ExplainPage:N0} could not be rendered.";
                PageExplanation = null;
            }
            else
            {
                var repair = Folder is null
                    ? null
                    : await _service.RecoveredAsync(Folder, SelectedDocument.Path, ExplainPage, CancellationToken.None)
                        .ConfigureAwait(true);
                Status = $"Page {ExplainPage:N0} of {SelectedDocument.Title}, as the audit saw it.";
                PageExplanation = ExplainPageState(IsFlagged(SelectedDocument.PageRanges, ExplainPage), repair);
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not render that page: {ex.Message}";
        }
    }

    private bool CanExplain => SelectedDocument is not null && ExplainPage >= 1;

    /// <summary>What the page Show drew means, and whether anything needs doing about it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPageExplanation))]
    private string? _pageExplanation;

    public bool HasPageExplanation => !string.IsNullOrEmpty(PageExplanation);


    /// <summary>
    /// Says, for the page in the picture, what its colours mean for search and where it stands. The
    /// legend alone left it unclear whether anything was expected of whoever was looking (#74).
    /// </summary>
    public static string ExplainPageState(bool flagged, PageRepair? repair)
    {
        const string legend =
            "Grey: text already in the document. Black: everything else drawn on the page. " +
            "Red boxes: lettering the document's text is missing, which search could not find.";

        var state = repair is not null
            ? string.Format(
                CultureInfo.CurrentCulture,
                "Recovered on {0:d} ({1:N0} word(s)) and kept for search; the PDF is unchanged, so it still shows black. Nothing to do.",
                repair.RepairedUtc.LocalDateTime, repair.WordCount)
            : flagged
                ? "Not recovered yet: tick the document and press Recover text."
                : "Not flagged: the document's text covers this page. Nothing to do.";

        return legend + "\n" + state;
    }
    /// <summary>Whether a page is in a list such as "14,16,21-26".</summary>
    public static bool IsFlagged(string? ranges, int page)
    {
        foreach (var part in (ranges ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                && int.TryParse(bounds[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var to)
                && page >= from && page <= to)
            {
                return true;
            }
        }

        return false;
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    public void Cancel()
    {
        State = DoctorState.Cancelling;
        Status = "Stopping after the page in flight...";
        _cancellation?.Cancel();
    }

    /// <summary>
    /// Ticks every row that still has work outstanding.
    ///
    /// <para>
    /// Parameterless rather than taking a flag, because a XAML CommandParameter arrives as a string
    /// and binding it to a bool argument fails at run time rather than at build time — which is
    /// exactly the class of defect the no-logic-in-the-view rule exists to avoid.
    /// </para>
    /// </summary>
    [RelayCommand]
    public void SelectEverything() => SelectAll(true);

    /// <summary>Unticks every row, for picking a handful out of several hundred.</summary>
    [RelayCommand]
    public void SelectNothing() => SelectAll(false);

    /// <summary>Ticks or unticks every row currently shown.</summary>
    public void SelectAll(bool selected)
    {
        _ticking = true;
        try
        {
            foreach (var row in Documents)
            {
                // A document with nothing outstanding has nothing to do, so leaving it ticked would
                // only make the count lie.
                row.Selected = selected && row.OutstandingPages > 0;
            }
        }
        finally
        {
            _ticking = false;
        }

        RecountSelected();
    }

    /// <summary>Set while All or None ticks rows in bulk, which should not move what Show draws.</summary>
    private bool _ticking;

    /// <summary>Re-reads the store without narrating it, after something has changed underneath.</summary>
    private async Task ReloadQuietlyAsync()
    {
        if (string.IsNullOrWhiteSpace(Folder))
            return;

        try
        {
            Show(await _service.ReadAsync(Folder, CancellationToken.None).ConfigureAwait(true), quiet: true);
        }
        catch (Exception)
        {
            // The reload is a courtesy after the work is done; failing it must not overwrite the
            // message that says what the work achieved.
        }
    }

    private void Show(DoctorStanding standing, bool quiet = false)
    {
        var previous = Documents.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);

        Documents.Clear();
        foreach (var finding in standing.Findings)
        {
            if (previous.TryGetValue(finding.Path, out var row))
            {
                row.Update(finding);
                Documents.Add(row);
                continue;
            }

            var added = new DoctorRow(finding);
            added.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(DoctorRow.Selected))
                    return;

                RecountSelected();

                // Ticking a document is how anyone picks one, so it is the one Show draws too. It was
                // only the highlighted row, which a tick does not move, so Show stayed disabled (#74).
                if (!_ticking && added.Selected)
                    SelectedDocument = added;
            };
            Documents.Add(added);
        }

        RecountSelected();

        var summary = standing.Summary;
        Headline = summary.DocumentsAudited == 0
            ? "This library has not been audited."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{summary.DocumentsAudited:N0} document(s) audited; {summary.DocumentsFlagged:N0} with " +
                $"pages whose text layer is incomplete, {summary.FlaggedPages:N0} page(s) in total " +
                $"({summary.DrawnPages:N0} drawn rather than photographed), {summary.RepairedPages:N0} " +
                $"recovered, {summary.OutstandingPages:N0} outstanding.");

        if (quiet)
            return;

        Status = summary.DocumentsAudited == 0
            ? "No audit yet. Run one, or run `manualforge doctor` over the whole library first."
            : summary.OutstandingPages == 0
                ? "Nothing to do here: every flagged page has had its text recovered. If any was recovered since the search index was last built, build it again on the Search tab so search can find it."
                : "Tick the documents to recover text from, then press Recover text.";
    }

    private void RecountSelected() => SelectedCount = Documents.Count(d => d.Selected);
}
