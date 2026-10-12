#:project ../src/ManualForge.Core/ManualForge.Core.csproj
#:property TargetFramework=net10.0-windows
#:property RuntimeIdentifier=win-x64
// For tools/compare-installed-build.ps1 (#26): every word of each PDF's text layer, one per line, in page order.
using UglyToad.PdfPig;
foreach (var f in args)
{
    using var pdf = PdfDocument.Open(f);
    foreach (var p in pdf.GetPages())
        foreach (var w in p.GetWords())
            Console.WriteLine(w.Text);
}
