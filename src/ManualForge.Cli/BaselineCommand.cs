using System.Globalization;
using ManualForge.Core.Benchmarking;
using Microsoft.Extensions.Logging;

namespace ManualForge.Cli;

/// <summary>
/// Measures where a word's baseline sits relative to its ink, so that
/// <c>TextLayerOptions.BaselineOffsetFraction</c> can be a measurement rather than a guess.
///
/// <para>
/// The recogniser boxes a word's ink; the text layer must be written on the word's baseline. For
/// <c>hello</c> those are the same line and for <c>happy</c> they are not, and one constant has to
/// serve both. This says what the constant should be, in the typefaces this corpus actually uses.
/// </para>
/// </summary>
internal static class BaselineCommand
{
    public static int Run(CommandLine arguments, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var library = arguments.Get("library")
            ?? arguments.Positional(0)
            ?? throw new ArgumentException("Give --library <folder> to take pages from.");

        if (!Directory.Exists(library))
            throw new DirectoryNotFoundException($"No such folder: {library}");

        var count = arguments.GetInt("pages") ?? 12;
        var dpi = arguments.GetInt("dpi") ?? 300;

        Console.WriteLine($"Library : {library}");
        Console.WriteLine($"Pages   : up to {count}, born-digital, rendered at {dpi} dpi");
        Console.WriteLine();
        Console.WriteLine("A born-digital page knows where its own baselines are, so this needs no");
        Console.WriteLine("recogniser and no GPU. It is a question about geometry, not about reading.");
        Console.WriteLine();

        var finder = new PublisherTruth(loggerFactory.CreateLogger<PublisherTruth>());
        var pages = finder.Select(library, count, arguments.GetInt("seed") ?? 1, cancellationToken);

        if (pages.Count == 0)
        {
            Console.Error.WriteLine("No born-digital page in this library qualifies.");
            return 1;
        }

        var probe = new BaselineProbe();
        var samples = new List<BaselineSample>();

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var measured = probe.Measure(page.ManualPath, page.PageNumber, dpi);
            samples.AddRange(measured);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {Path.GetFileNameWithoutExtension(page.ManualPath),-44} p{page.PageNumber,-5} {measured.Count,5:N0} word(s)"));
        }

        if (samples.Count == 0)
        {
            Console.Error.WriteLine("No word on those pages could be measured.");
            return 1;
        }

        var pointsPerPixel = 72.0 / dpi;
        var withDescender = samples.Where(s => s.HasDescender).ToArray();
        var without = samples.Where(s => !s.HasDescender).ToArray();

        Console.WriteLine();
        Console.WriteLine($"Measured {samples.Count:N0} word(s) across {pages.Count} page(s).");
        Console.WriteLine();
        Console.WriteLine("  Words                       count   mean fraction   median   mean error at 0.0");
        Console.WriteLine("  " + new string('-', 76));

        Print("with a descender", withDescender, pointsPerPixel);
        Print("without one", without, pointsPerPixel);
        Print("all", samples, pointsPerPixel);

        var recommendation = BaselineProbe.Recommend(samples, pointsPerPixel);

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Best single constant : {recommendation.Fraction:F2}, costing {recommendation.MeanErrorPt:F2} pt " +
            $"of mean baseline error"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"The current default  : 0.00, costing {recommendation.MeanErrorAtZeroPt:F2} pt"));
        Console.WriteLine();

        // A sanity check worth printing rather than assuming: a word with no descender sits on its
        // ink, so its fraction must come out near zero. If it does not, the measurement is wrong
        // and nothing below it means anything.
        if (without.Length > 0)
        {
            var check = without.Average(s => s.Fraction);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Sanity: words without a descender average {check:F3}, and should be near zero."));
        }

        return 0;
    }

    private static void Print(string label, IReadOnlyList<BaselineSample> samples, double pointsPerPixel)
    {
        if (samples.Count == 0)
            return;

        var ordered = samples.Select(s => s.Fraction).Order().ToArray();
        var median = ordered[ordered.Length / 2];
        var errorAtZero = samples.Average(s => Math.Abs(s.Fraction) * s.InkHeightPx * pointsPerPixel);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {label,-26}{samples.Count,6:N0}{samples.Average(s => s.Fraction),16:F3}{median,9:F3}{errorAtZero,20:F2} pt"));
    }
}
