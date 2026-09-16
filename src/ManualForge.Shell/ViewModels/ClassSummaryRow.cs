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
public sealed partial class ClassSummaryRow(TextClass textClass, int files, long pages, ClassAction action)
    : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public TextClass TextClass { get; } = textClass;

    public int Files { get; } = files;

    public long Pages { get; } = pages;

    private ClassAction _action = action;

    public ClassAction Action
    {
        get => _action;
        set => SetProperty(ref _action, value);
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
                g.Class, g.Rows.Length, g.Rows.Sum(r => (long)r.PageCount), policy.ActionFor(g.Class)))
            .ToArray();
    }
}
