#:project ../src/ManualForge.Core/ManualForge.Core.csproj
#:property TargetFramework=net10.0-windows
#:property RuntimeIdentifier=win-x64
#:property Nullable=enable
#:property NoWarn=CS0618

// How well each way of grouping letters into words reads a page, scored against pdftotext (#47).
// For every page in the sample: the share of pdftotext's words (three letters or more) a grouping also
// produces (recall), and the share of its own words pdftotext agrees with (precision).
//
// usage: dotnet run tools/measure-word-grouping.cs -- <sample.tsv> <pdftotext.exe> [details.tsv]
//   sample.tsv: one page per line - pdf path, page number, a label to total by (e.g. spaced / ordinary)
//   pdftotext ships with Git for Windows: C:\Program Files\Git\mingw64\bin\pdftotext.exe
using System.Diagnostics;
using System.Text.RegularExpressions;
using ManualForge.Core.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

var sample = File.ReadAllLines(args[0]).Select(l => l.Split('\t')).Where(r => r.Length >= 3).ToArray();
var pdftotext = args[1];
var token = new Regex(@"[A-Za-z0-9]{3,}");

Dictionary<string, int> Tokens(string text)
{
    var d = new Dictionary<string, int>();
    foreach (Match m in token.Matches(text)) { var k = m.Value.ToLowerInvariant(); d[k] = d.GetValueOrDefault(k) + 1; }
    return d;
}

static int Common(Dictionary<string, int> a, Dictionary<string, int> b) =>
    a.Sum(kv => Math.Min(kv.Value, b.GetValueOrDefault(kv.Key)));

string Reference(string pdf, int page)
{
    var psi = new ProcessStartInfo(pdftotext, $"-f {page} -l {page} -raw -enc UTF-8 \"{pdf}\" -")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
    using var p = Process.Start(psi)!;
    var text = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return text;
}

var candidates = new (string Name, Func<Page, IEnumerable<Word>> Words)[]
{
    ("PdfPig default", p => p.GetWords()),
    ("nearest neighbour", p => p.GetWords(NearestNeighbourWordExtractor.Instance)),
    ("PageWords", PageWords.Of),
};

var totals = new Dictionary<(string Label, string Name), (long Common, long Ref, long Ours, double Ms, int Pages)>();
var details = new List<string>();
foreach (var row in sample)
{
    var (pdf, pageNo, label) = (row[0], int.Parse(row[1]), row[2]);
    try
    {
        var reference = Tokens(Reference(pdf, pageNo));
        if (reference.Values.Sum() < 20) continue;
        using var doc = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true });
        var page = doc.GetPage(pageNo);
        var line = new List<string> { Path.GetFileName(pdf), pageNo.ToString(), label };
        foreach (var (name, words) in candidates)
        {
            var sw = Stopwatch.StartNew();
            var ours = Tokens(string.Join(' ', words(page).Select(w => w.Text)));
            sw.Stop();
            var common = Common(reference, ours);
            var t = totals.GetValueOrDefault((label, name));
            totals[(label, name)] = (t.Common + common, t.Ref + reference.Values.Sum(), t.Ours + ours.Values.Sum(), t.Ms + sw.Elapsed.TotalMilliseconds, t.Pages + 1);
            line.Add($"{common / (double)reference.Values.Sum():F3}");
        }
        details.Add(string.Join('\t', line));
    }
    catch (Exception ex) { Console.Error.WriteLine($"{Path.GetFileName(pdf)} p{pageNo}: {ex.Message}"); }
}

Console.WriteLine($"{"sample",-10} {"grouping",-18} {"pages",5} {"recall",7} {"precision",9} {"ms/page",8}");
foreach (var label in totals.Keys.Select(k => k.Label).Distinct())
    foreach (var (name, _) in candidates)
    {
        var t = totals[(label, name)];
        Console.WriteLine($"{label,-10} {name,-18} {t.Pages,5} {t.Common / (double)t.Ref,7:P1} {t.Common / (double)t.Ours,9:P1} {t.Ms / t.Pages,8:F1}");
    }
if (args.Length > 2) File.WriteAllLines(args[2], details);
