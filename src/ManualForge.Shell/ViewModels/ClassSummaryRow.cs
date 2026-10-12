using ManualForge.Core.Classification;
using ManualForge.Core.State;

namespace ManualForge.Shell.ViewModels;

/// <summary>
/// One row of the summary shown before any work is committed to: a class, how much of the library
/// is in it, and what the current policy would do about it.
///
/// The action is editable, because the specification asks to choose per class whether to OCR, skip
/// or strip-and-redo, and because the numbers beside it are what should inform that choice.
/// </summary>
public sealed partial class ClassSummaryRow(TextClass textClass, int files, long pages, ClassAction action, int completed = 0)
    : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public TextClass TextClass { get; } = textClass;

    public int Files { get; } = files;

    public long Pages { get; } = pages;

    /// <summary>Files of this class already finished, which a run leaves alone.</summary>
    public int Completed { get; } = completed;

    /// <summary>
    /// The Done column. The class is what a file was when first classified, so a finished library still
    /// lists 166 ImageOnly files with Ocr beside them, which read as 166 files not yet OCR'd.
    /// </summary>
    public string Done => Completed == 0 ? string.Empty : $"{Completed:N0} of {Files:N0}";

    private ClassAction _action = action;

    public ClassAction Action
    {
        get => _action;
        set
        {
            if (SetProperty(ref _action, value))
                OnPropertyChanged(nameof(SelectedAction));
        }
    }

    /// <summary>
    /// What the table's drop-down is bound to. While WinUI fills a row it sets the drop-down's
    /// selection to nothing before its items arrive, and a two-way binding straight to
    /// <see cref="Action"/> pushed that nothing into an enum: the app died with a
    /// NullReferenceException on 12 October 2026, filling the class table after a survey. Anything
    /// that is not an action is ignored here.
    /// </summary>
    public object? SelectedAction
    {
        get => _action;
        set
        {
            if (value is ClassAction action)
                Action = action;
        }
    }

    public IReadOnlyList<ClassAction> AvailableActions { get; } =
        [ClassAction.Skip, ClassAction.Ocr, ClassAction.StripAndRedo];

    /// <summary>What the class means, so the table explains itself rather than needing the README.</summary>
    public string Explanation => TextClass switch
    {
        TextClass.ImageOnly => "No text layer at all. OCR is pure gain.",
        TextClass.SuspectText => "Text is there but looks garbled.",
        TextClass.UnreadableTextLayer =>
            "The pages draw glyphs that will not decode. Adding a second layer would make it worse.",
        TextClass.ProbablyGood => "Clean enough to be doubtful about, not bad enough to condemn.",
        TextClass.GoodText => "Text is there and clean. Leave it alone.",
        TextClass.Unreadable => "The file could not be read at all.",
        _ => string.Empty,
    };

    public static IReadOnlyList<ClassSummaryRow> From(
        IReadOnlyList<FileRecord> records, ClassificationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(policy);

        // Records for files that have gone are excluded, so the table describes the library as it
        // stands rather than as it once did. They are reported separately and never silently.
        var present = records.Where(r => r.Status != FileStatus.Missing).ToArray();

        return Enum.GetValues<TextClass>()
            .Select(c => (Class: c, Rows: present.Where(r => r.TextClass == c).ToArray()))
            .Where(g => g.Rows.Length > 0)
            .Select(g => new ClassSummaryRow(
                g.Class, g.Rows.Length, g.Rows.Sum(r => (long)r.PageCount), policy.ActionFor(g.Class),
                g.Rows.Count(r => r.Status == FileStatus.Completed)))
            .ToArray();
    }
}
