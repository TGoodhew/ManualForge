using ManualForge.Core.Geometry;

namespace ManualForge.Core.Ocr;

/// <summary>One recognised word with its box in image pixels, top-left origin.</summary>
public sealed record RecognisedWord(string Text, RectD BoxPx, double Confidence)
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(Text) && !BoxPx.IsDegenerate;
}

/// <summary>One recognised line, holding the words that make it up.</summary>
public sealed record RecognisedLine(string Text, RectD BoxPx, double Confidence, IReadOnlyList<RecognisedWord> Words);

/// <summary>The OCR result for a single page, in image pixel coordinates.</summary>
public sealed record RecognisedPage(
    int PageNumber,
    int PixelWidth,
    int PixelHeight,
    IReadOnlyList<RecognisedLine> Lines,
    string ExecutionProvider,
    TimeSpan Duration)
{
    public IEnumerable<RecognisedWord> Words => Lines.SelectMany(l => l.Words);

    public int WordCount => Lines.Sum(l => l.Words.Count);

    public double MeanConfidence => Lines.Count == 0 ? 0 : Lines.Average(l => l.Confidence);
}
