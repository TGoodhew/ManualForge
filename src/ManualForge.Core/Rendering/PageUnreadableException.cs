namespace ManualForge.Core.Rendering;

/// <summary>
/// A page that cannot be read for a reason that lies in the page itself, so every attempt will fail
/// the same way.
///
/// <para>
/// The distinction it exists to draw is the one #21 turned on. A failure that is transient or
/// systemic - the card fell over, memory ran out, the model would not load - must fail the whole
/// document and leave the original where it was, because retrying later will work. A failure that
/// belongs to one page will not change on a retry, and failing the document for it threw away
/// every other page: a 120-page book lost 116 finished pages to one fold-out.
/// </para>
///
/// <para>
/// Only failures known to be page-specific become this. Anything else stays an ordinary exception
/// and keeps failing the document, because guessing wrong in this direction would replace an
/// original with a copy missing text that a retry would have recovered.
/// </para>
/// </summary>
public sealed class PageUnreadableException(int pageNumber, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The page, 1-based.</summary>
    public int PageNumber { get; } = pageNumber;
}
