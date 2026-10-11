#:project ../src/ManualForge.Core/ManualForge.Core.csproj
#:property TargetFramework=net10.0-windows
#:property RuntimeIdentifier=win-x64
// For tools/probe-arena.cs (#34): page sizes at 300 dpi, in order, for each PDF given: "file page widthPx heightPx".
using UglyToad.PdfPig;
foreach (var f in args)
{
    using var pdf = PdfDocument.Open(f);
    foreach (var p in pdf.GetPages())
        Console.WriteLine($"{Path.GetFileName(f)}\t{p.Number}\t{(int)Math.Round(p.Width * 300 / 72)}\t{(int)Math.Round(p.Height * 300 / 72)}");
}
