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
    private int _documentsDone;

    [ObservableProperty]
    private int _documentsTotal;

    [ObservableProperty]
    private int _pagesDone;

    [ObservableProperty]
    private int _pagesTotal;

    [ObservableProperty]
    private string? _currentFile;

    [ObservableProperty]
    private string? _currentPage;

    [ObservableProperty]
    private DoctorRow? _selectedDocument;

    /// <summary>
    /// The page whose diagnostic picture to show. A number rather than a picker, because what
    /// somebody wants to check is "page 40 of this manual", which is what the report just told them.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExplainCommand))]
    private int _explainPage;

    /// <summary>
    /// The picture, as PNG bytes. Bytes rather than an image type so this stays free of any
    /// dependency on a UI framework and can be asserted in a test.
    /// </summary>
    [ObservableProperty]
    private byte[]? _diagnostic;

    public ObservableCollection<DoctorRow> Documents { get; } = [];

    public ObservableCollection<string> Recovered { get; } = [];

    public bool IsBusy => State is not DoctorState.Idle;

    public bool IsIdle => State is DoctorState.Idle;

    public double PageProgress => PagesTotal <= 0 ? 0 : 100.0 * PagesDone / PagesTotal;

    private bool CanAct => IsIdle && !string.IsNullOrWhiteSpace(Folder);

    private bool CanRepair => CanAct && SelectedCount > 0;

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
        Status = "Auditing. Nothing is being changed.";
        DocumentsDone = DocumentsTotal = 0;

        var progress = new DispatchedProgress<DoctorProgress>(_dispatcher, p =>
        {
            DocumentsDone = p.DocumentsDone;
            DocumentsTotal = p.DocumentsTotal;
            CurrentFile = Path.GetFileName(p.Path);
            Status =
                $"Auditing: {p.DocumentsDone:N0} of {p.DocumentsTotal:N0} documents, " +
                $"{p.FlaggedPages:N0} page(s) flagged so far.";
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

            Status = Diagnostic is null
                ? $"Page {ExplainPage:N0} could not be rendered."
                : "Grey is ink an extracted glyph accounts for; black is ink nothing accounts for; " +
                  "the boxes are what was counted as lettering.";
        }
        catch (Exception ex)
        {
            Status = $"Could not render that page: {ex.Message}";
        }
    }

    private bool CanExplain => SelectedDocument is not null && ExplainPage >= 1;

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
        foreach (var row in Documents)
        {
            // A document with nothing outstanding has nothing to do, so leaving it ticked would
            // only make the count lie.
            row.Selected = selected && row.OutstandingPages > 0;
        }

        RecountSelected();
    }

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
                if (e.PropertyName == nameof(DoctorRow.Selected))
                    RecountSelected();
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
                ? "Every flagged page has had its text recovered. Build the index to make it searchable."
                : "Tick the documents to recover text from, then press Recover text.";
    }

    private void RecountSelected() => SelectedCount = Documents.Count(d => d.Selected);
}
