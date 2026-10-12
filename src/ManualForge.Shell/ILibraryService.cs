using ManualForge.Core.Classification;
using ManualForge.Core.Ocr;
using ManualForge.Core.Pipeline;
using ManualForge.Core.State;

namespace ManualForge.Shell;

/// <summary>
/// Everything the shell needs from the engine room, behind one seam.
///
/// It exists so the view models can be driven in a test without an OCR model, a GPU or a file on
/// disk. The real implementation is a thin wrapper over <see cref="LibraryProcessor"/>; the fake
/// one in the tests is what makes "cancel mid-run leaves the error list intact" a thing that can be
/// asserted rather than clicked through.
/// </summary>
public interface ILibraryService
{
    /// <summary>Classifies a folder without changing anything.</summary>
    Task<IReadOnlyList<FileRecord>> SurveyAsync(
        string root,
        ClassificationPolicy policy,
        IProgress<DocumentClassification>? progress,
        CancellationToken cancellationToken);

    /// <summary>Processes everything the policy marks for work.</summary>
    Task<IReadOnlyList<FileOutcome>> RunAsync(
        string root,
        ClassificationPolicy policy,
        bool dryRun,
        IProgress<FileOutcome>? files,
        IProgress<PipelinePageProgress>? pages,
        CancellationToken cancellationToken);

    /// <summary>
    /// Forgets files that are no longer on disk: their records in the queue, their findings in the
    /// audit and their pages in the search index. Returns each file forgotten from any of them.
    /// </summary>
    Task<IReadOnlyList<string>> TrimMissingAsync(string root, CancellationToken cancellationToken);

    /// <summary>The card, or null when there is nothing to report.</summary>
    GpuMemory? ReadGpu();
}
