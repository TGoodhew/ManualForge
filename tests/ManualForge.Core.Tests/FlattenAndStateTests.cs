using System.Text;
using ManualForge.Core.Classification;
using ManualForge.Core.Pdf;
using ManualForge.Core.State;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace ManualForge.Core.Tests;

public class FlattenTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public FlattenTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Builds a multi-page document carrying a real image, so the flatten check has image streams
    /// to compare rather than just page boxes.
    /// </summary>
    private string CreateDocument(string name, int pages = 3, string? ownerPassword = null, int rotation = 0)
    {
        var path = Path.Combine(_directory, name);
        using var document = new PdfDocument();

        for (var i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            page.Rotate = rotation;

            // A small bitonal-ish image, embedded as PNG so it has a real filter to preserve.
            var imagePath = Path.Combine(_directory, $"img{i}.png");
            if (!File.Exists(imagePath))
                File.WriteAllBytes(imagePath, MakePng(60 + i * 10, 40 + i * 10));

            using var gfx = XGraphics.FromPdfPage(page);
            using var image = XImage.FromFile(imagePath);
            gfx.DrawImage(image, 50, 50, 300, 200);
        }

        if (ownerPassword is not null)
        {
            var security = document.SecuritySettings;
            security.OwnerPassword = ownerPassword;
            security.PermitModifyDocument = false;
            security.PermitPrint = true;
        }

        document.Save(path);
        return path;
    }

    /// <summary>A minimal valid greyscale PNG, written by hand to avoid a drawing dependency.</summary>
    private static byte[] MakePng(int width, int height)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBe(ihdr, 0, width);
        WriteBe(ihdr, 4, height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 0;    // greyscale
        WriteChunk(writer, "IHDR", ihdr);

        // One filter byte per row, then a simple gradient so the image is not uniformly blank.
        var raw = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = 0;
            for (var x = 0; x < width; x++)
                raw[y * (width + 1) + 1 + x] = (byte)((x * 7 + y * 13) % 256);
        }

        using var deflated = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(deflated, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        WriteChunk(writer, "IDAT", deflated.ToArray());
        WriteChunk(writer, "IEND", []);

        writer.Flush();
        return ms.ToArray();
    }

    private static void WriteBe(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteChunk(BinaryWriter writer, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBe(length, 0, data.Length);
        writer.Write(length);

        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        writer.Write(typeAndData);

        var crc = Crc32(typeAndData);
        var crcBytes = new byte[4];
        WriteBe(crcBytes, 0, unchecked((int)crc));
        writer.Write(crcBytes);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void AnOwnerPasswordBlocksModificationAndIsDetected()
    {
        var path = CreateDocument("locked.pdf", ownerPassword: "secret");
        var capabilities = PdfInspector.Inspect(path);

        Assert.False(capabilities.CanOpenForModify);
        Assert.True(capabilities.CanOpenForImport);
        Assert.True(capabilities.NeedsFlattening);
        Assert.Equal(ModificationBlocker.OwnerPassword, capabilities.Blocker);
        Assert.True(capabilities.HasEncryptDictionary);
    }

    [Fact]
    public void AnOrdinaryDocumentNeedsNoFlattening()
    {
        var path = CreateDocument("plain.pdf");
        var capabilities = PdfInspector.Inspect(path);

        Assert.True(capabilities.CanOpenForModify);
        Assert.False(capabilities.NeedsFlattening);
        Assert.Equal(ModificationBlocker.None, capabilities.Blocker);
    }

    [Fact]
    public void FlatteningClearsTheOwnerPasswordAndTheResultIsModifiable()
    {
        var source = CreateDocument("locked2.pdf", pages: 4, ownerPassword: "secret");
        var output = Path.Combine(_directory, "flat.pdf");

        var result = new PdfFlattener().Flatten(source, output);

        Assert.True(result.IsVerified, string.Join("; ", result.Discrepancies));
        Assert.Equal(4, result.PageCount);
        Assert.Equal(ModificationBlocker.OwnerPassword, result.BlockerCleared);

        var after = PdfInspector.Inspect(output);
        Assert.True(after.CanOpenForModify);
        Assert.False(after.HasEncryptDictionary);
    }

    [Fact]
    public void FlatteningPreservesPageCountGeometryAndImageStreams()
    {
        var source = CreateDocument("geo.pdf", pages: 3, ownerPassword: "secret", rotation: 90);
        var output = Path.Combine(_directory, "geo-flat.pdf");

        var before = PdfFlattener.Fingerprint(source);
        new PdfFlattener().Flatten(source, output);
        var after = PdfFlattener.Fingerprint(output);

        Assert.Empty(PdfFlattener.Compare(before, after));
        Assert.Equal(before.Count, after.Count);

        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Rotation, after[i].Rotation);
            Assert.Equal(before[i].WidthPt, after[i].WidthPt, 0.05);

            // The pixel dimensions and compression of every image must be untouched: that is the
            // difference between a structural rebuild and a lossy re-encode.
            Assert.Equal(before[i].Images.Count, after[i].Images.Count);
            Assert.Equal(before[i].Images, after[i].Images);
        }
    }

    [Fact]
    public void TheFlattenedFileIsNotMeaningfullyLargerThanTheSource()
    {
        // A re-encode would change the size dramatically in one direction or the other. A
        // structural rebuild lands within a few percent.
        var source = CreateDocument("size.pdf", pages: 5, ownerPassword: "secret");
        var output = Path.Combine(_directory, "size-flat.pdf");

        var result = new PdfFlattener().Flatten(source, output);

        Assert.InRange(result.SizeRatio, 0.5, 1.5);
    }

    [Fact]
    public void ComparisonCatchesAChangedPageCount()
    {
        var a = PdfFlattener.Fingerprint(CreateDocument("a.pdf", pages: 3));
        var b = PdfFlattener.Fingerprint(CreateDocument("b.pdf", pages: 2));

        var problems = PdfFlattener.Compare(a, b);
        Assert.Contains(problems, p => p.Contains("Page count", StringComparison.Ordinal));
    }

    [Fact]
    public void ComparisonCatchesAReEncodedImage()
    {
        var before = PdfFlattener.Fingerprint(CreateDocument("c.pdf", pages: 1));

        // Same page, but pretend the image came back at half the resolution.
        var shrunk = before.Select(p => p with
        {
            Images = p.Images.Select(i => (i.PixelWidth / 2, i.PixelHeight / 2, i.Filter)).ToArray(),
        }).ToArray();

        var problems = PdfFlattener.Compare(before, shrunk);
        Assert.Contains(problems, p => p.Contains("re-encoded", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesToFlattenAFileOverItself()
    {
        var path = CreateDocument("self.pdf");
        Assert.Throws<InvalidOperationException>(() => new PdfFlattener().Flatten(path, path));
    }

    [Fact]
    public void AnEmptyAcroFormIsNotMistakenForASignature()
    {
        // Regression from the repair run: detection scanned for the bytes "/SigFlags" and refused
        // any file containing them. An AcroForm with /SigFlags 0 and no fields contains that string
        // and is not signed, so real work was being skipped silently.
        var path = CreateDocument("emptyform.pdf");

        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var acroForm = new PdfDictionary(document);
            acroForm.Elements["/SigFlags"] = new PdfInteger(0);
            acroForm.Elements["/Fields"] = new PdfArray(document);
            document.Internals.Catalog.Elements["/AcroForm"] = acroForm;
            document.Save(path);
        }

        Assert.False(PdfInspector.HasSignatureMarker(path));
        Assert.Equal(ModificationBlocker.None, PdfInspector.Inspect(path).Blocker);
    }

    [Fact]
    public void AnUnsignedSignatureFieldIsNotASignature()
    {
        // A form that declares a signature field nobody has signed yet is still safe to modify.
        var path = CreateDocument("unsignedfield.pdf");

        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var field = new PdfDictionary(document);
            field.Elements["/FT"] = new PdfName("/Sig");
            field.Elements["/T"] = new PdfString("Signature1");
            // No /V, so nothing has been signed.

            var fields = new PdfArray(document);
            fields.Elements.Add(field);

            var acroForm = new PdfDictionary(document);
            acroForm.Elements["/SigFlags"] = new PdfInteger(0);
            acroForm.Elements["/Fields"] = fields;
            document.Internals.Catalog.Elements["/AcroForm"] = acroForm;
            document.Save(path);
        }

        Assert.False(PdfInspector.HasSignatureMarker(path));
    }

    [Fact]
    public void AFieldCarryingAByteRangeIsRecognisedAsSigned()
    {
        var path = CreateDocument("signed.pdf");

        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var byteRange = new PdfArray(document);
            byteRange.Elements.Add(new PdfInteger(0));
            byteRange.Elements.Add(new PdfInteger(840));
            byteRange.Elements.Add(new PdfInteger(960));
            byteRange.Elements.Add(new PdfInteger(240));

            var value = new PdfDictionary(document);
            value.Elements["/Type"] = new PdfName("/Sig");
            value.Elements["/ByteRange"] = byteRange;

            var field = new PdfDictionary(document);
            field.Elements["/FT"] = new PdfName("/Sig");
            field.Elements["/V"] = value;

            var fields = new PdfArray(document);
            fields.Elements.Add(field);

            var acroForm = new PdfDictionary(document);
            acroForm.Elements["/SigFlags"] = new PdfInteger(3);
            acroForm.Elements["/Fields"] = fields;
            document.Internals.Catalog.Elements["/AcroForm"] = acroForm;
            document.Save(path);
        }

        Assert.True(PdfInspector.HasSignatureMarker(path));
        Assert.Equal(ModificationBlocker.Signature, PdfInspector.Inspect(path).Blocker);
    }

    [Fact]
    public void AnOrdinaryScanIsNotMistakenForASignature()
    {
        var path = CreateDocument("ordinary.pdf");
        Assert.False(PdfInspector.HasSignatureMarker(path));
    }

    [Fact]
    public void StrippingRemovesTextButLeavesTheImage()
    {
        var path = CreateDocument("withtext.pdf", pages: 2);

        // Put some real text on the pages, then strip it.
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            foreach (var page in document.Pages.Cast<PdfPage>())
            {
                var content = page.Contents.AppendContent();
                content.CreateStream(Encoding.ASCII.GetBytes(
                    "BT /F1 12 Tf 100 700 Td (some stale ocr text) Tj ET\n"));
            }
            document.Save(path);
        }

        var beforeImages = PdfFlattener.Fingerprint(path).Sum(p => p.Images.Count);

        var stripped = Path.Combine(_directory, "stripped.pdf");
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var result = TextLayerStripper.Strip(document);
            Assert.Equal(2, result.PagesChanged);
            Assert.Equal(2, result.TextBlocksRemoved);
            document.Save(stripped);
        }

        // The images must survive untouched...
        Assert.Equal(beforeImages, PdfFlattener.Fingerprint(stripped).Sum(p => p.Images.Count));

        // ...and no text may remain.
        using var check = UglyToad.PdfPig.PdfDocument.Open(stripped);
        Assert.Equal(0, check.GetPages().Sum(p => p.Letters.Count));
    }

    [Fact]
    public void StrippingLeavesADocumentWithNoTextAlone()
    {
        var path = CreateDocument("notext.pdf", pages: 2);
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);

        var result = TextLayerStripper.Strip(document);

        Assert.Equal(0, result.PagesChanged);
        Assert.Equal(0, result.TextBlocksRemoved);
    }
}

public class JobStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ManualForge.Tests", Guid.NewGuid().ToString("N"));

    public JobStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string MakeFile(string name, string content = "hello")
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private JobStore NewStore(string name = "state.db") => new(Path.Combine(_directory, name));

    [Fact]
    public void RegisteringAFileRecordsIt()
    {
        var file = MakeFile("a.pdf");
        using var store = NewStore();

        var record = store.Register(file);

        Assert.Equal(FileStatus.Discovered, record.Status);
        Assert.Equal(new FileInfo(file).Length, record.Fingerprint.SizeBytes);
    }

    [Fact]
    public void StateSurvivesReopeningTheDatabase()
    {
        var file = MakeFile("b.pdf");
        var dbName = "persist.db";

        using (var store = NewStore(dbName))
        {
            store.Register(file);
            store.SetStatus(file, FileStatus.Completed);
        }

        using (var store = NewStore(dbName))
        {
            Assert.Equal(FileStatus.Completed, store.Find(file)!.Status);
        }
    }

    [Fact]
    public void AFinishedLibraryHasNothingOutstanding()
    {
        var a = MakeFile("c.pdf");
        var b = MakeFile("d.pdf");
        using var store = NewStore();

        foreach (var file in new[] { a, b })
        {
            store.Register(file);
            store.SetStatus(file, FileStatus.Completed);
        }

        Assert.Empty(store.Outstanding());
    }

    [Fact]
    public void AnInterruptedFileResumesFromThePageItReached()
    {
        var file = MakeFile("e.pdf");
        using var store = NewStore();
        store.Register(file);
        store.SetStatus(file, FileStatus.InProgress);

        foreach (var page in new[] { 1, 2, 3, 5 })
            store.RecordPage(file, page, PageStatus.Completed, wordsWritten: 100);
        store.RecordPage(file, 4, PageStatus.Failed, error: "boom");

        var completed = store.CompletedPages(file);

        Assert.Equal([1, 2, 3, 5], completed.Order());
        Assert.DoesNotContain(4, completed);

        // And the file is still outstanding, because it never completed.
        store.RecordClassification(file, Classification(file), Capabilities(file), ClassAction.Ocr);
        store.SetStatus(file, FileStatus.InProgress);
        Assert.Single(store.Outstanding());
    }

    [Fact]
    public void AChangedSourceFileResetsItsProgress()
    {
        var file = MakeFile("f.pdf", "original");
        using var store = NewStore();

        store.Register(file);
        store.SetStatus(file, FileStatus.Completed);
        store.RecordPage(file, 1, PageStatus.Completed);
        Assert.Single(store.CompletedPages(file));

        // Rewrite the source. Anything we recorded about it is now describing a different file.
        Thread.Sleep(10);
        File.WriteAllText(file, "completely different content, different length");

        var record = store.Register(file);

        Assert.Equal(FileStatus.Discovered, record.Status);
        Assert.Empty(store.CompletedPages(file));
    }

    [Fact]
    public void RestoringAnOriginalOverAFinishedFileMakesItOutstandingAgain()
    {
        // Regression from the first full library run. Processing replaces the file in place, but
        // the recorded fingerprint still described the source it replaced. So when originals were
        // restored to redo a batch, every one of them matched its stale fingerprint exactly, looked
        // unchanged and already finished, and nothing was reprocessed - while the summary still
        // claimed 155 files were marked for work.
        var file = MakeFile("restore.pdf", "the original scan, no text layer");
        var originalBytes = File.ReadAllBytes(file);

        using var store = NewStore();
        store.Register(file);
        store.RecordClassification(file, Classification(file), Capabilities(file), ClassAction.Ocr);

        // Processing replaces the file in place, then records completion.
        File.WriteAllText(file, "the searchable version, with a text layer added to it");
        store.UpdateFingerprint(file);
        store.SetStatus(file, FileStatus.Completed);
        Assert.Empty(store.Outstanding());

        // Now restore the original over it, exactly as the repair did.
        Thread.Sleep(10);
        File.WriteAllBytes(file, originalBytes);

        var record = store.Register(file);

        // Register alone resets it to Discovered and clears the action, because a file whose
        // contents changed has to be classified again before anything is decided about it. That
        // reclassification is what every run does before processing.
        Assert.Equal(FileStatus.Discovered, record.Status);
        Assert.Empty(store.CompletedPages(file));

        store.RecordClassification(file, Classification(file), Capabilities(file), ClassAction.Ocr);

        Assert.Single(store.Outstanding());
    }

    [Fact]
    public void WithoutUpdatingTheFingerprintAFinishedFileLooksChangedOnEveryRun()
    {
        // The other half of the same problem: if the fingerprint is never refreshed, the file we
        // wrote does not match what was recorded, so every subsequent run treats a finished file as
        // changed. Refreshing it on completion is what makes a re-run genuinely a no-op.
        var file = MakeFile("stable.pdf", "original");
        using var store = NewStore("stable.db");

        store.Register(file);
        store.RecordClassification(file, Classification(file), Capabilities(file), ClassAction.Ocr);

        File.WriteAllText(file, "searchable version");
        store.UpdateFingerprint(file);
        store.SetStatus(file, FileStatus.Completed);

        // Re-registering the untouched result must not reset anything.
        var again = store.Register(file);

        Assert.Equal(FileStatus.Completed, again.Status);
        Assert.Empty(store.Outstanding());
    }

    [Fact]
    public void AnUnchangedFileKeepsItsProgress()
    {
        var file = MakeFile("g.pdf");
        using var store = NewStore();

        store.Register(file);
        store.SetStatus(file, FileStatus.Completed);
        store.RecordPage(file, 1, PageStatus.Completed);

        var again = store.Register(file);

        Assert.Equal(FileStatus.Completed, again.Status);
        Assert.Single(store.CompletedPages(file));
    }

    [Fact]
    public void SkippedFilesAreNeverOutstanding()
    {
        var file = MakeFile("h.pdf");
        using var store = NewStore();

        store.Register(file);
        store.RecordClassification(file, Classification(file), Capabilities(file), ClassAction.Skip);

        Assert.Empty(store.Outstanding());
    }

    [Fact]
    public void SkippedFilesCanBeReconsideredWithoutDiscardingEverythingElse()
    {
        // Skipping is a decision, not a fact about the file. When the reason changes - a widened
        // policy, or a skip that turned out to be a false positive - the skipped files have to come
        // back without throwing away the record of everything already finished.
        var skipped = MakeFile("skipped.pdf");
        var finished = MakeFile("finished.pdf");

        using var store = NewStore("retry.db");

        store.Register(skipped);
        store.RecordClassification(skipped, Classification(skipped), Capabilities(skipped), ClassAction.Ocr);
        store.SetStatus(skipped, FileStatus.Skipped, "refused as signed");

        store.Register(finished);
        store.RecordClassification(finished, Classification(finished), Capabilities(finished), ClassAction.Ocr);
        store.UpdateFingerprint(finished);
        store.SetStatus(finished, FileStatus.Completed);

        Assert.Empty(store.Outstanding());

        var reset = store.ResetSkipped();

        Assert.Equal(1, reset);
        Assert.Equal(FileStatus.Discovered, store.Find(skipped)!.Status);
        Assert.Null(store.Find(skipped)!.Error);

        // The finished file must be untouched by this.
        Assert.Equal(FileStatus.Completed, store.Find(finished)!.Status);
    }

    [Fact]
    public void ClassificationNumbersRoundTrip()
    {
        var file = MakeFile("i.pdf");
        using var store = NewStore();
        store.Register(file);

        var classification = new DocumentClassification(
            file, 120, 8, 1234.5, 0.876, 0.234, TextClass.GoodText, "because", []);
        store.RecordClassification(file, classification, Capabilities(file), ClassAction.Skip);

        var record = store.Find(file)!;
        Assert.Equal(TextClass.GoodText, record.TextClass);
        Assert.Equal(120, record.PageCount);
        Assert.Equal(1234.5, record.AlphanumericPerPage, 3);
        Assert.Equal(0.876, record.PlausibleTokenRatio, 3);
        Assert.Equal(0.234, record.CommonWordShare, 3);
    }

    [Fact]
    public void PageProgressIsCounted()
    {
        var file = MakeFile("j.pdf");
        using var store = NewStore();
        store.Register(file);

        for (var page = 1; page <= 7; page++)
            store.RecordPage(file, page, PageStatus.Completed);

        Assert.Equal(7, store.Find(file)!.PagesCompleted);
    }

    [Fact]
    public void RecordingTheSamePageTwiceUpdatesRatherThanDuplicates()
    {
        var file = MakeFile("k.pdf");
        using var store = NewStore();
        store.Register(file);

        store.RecordPage(file, 1, PageStatus.Failed, error: "first try");
        store.RecordPage(file, 1, PageStatus.Completed, wordsWritten: 42);

        Assert.Single(store.CompletedPages(file));
        Assert.Equal(1, store.Find(file)!.PagesCompleted);
    }

    private static DocumentClassification Classification(string path)
        => new(path, 10, 8, 500, 0.9, 0.3, TextClass.ImageOnly, "test", []);

    private static PdfCapabilities Capabilities(string path)
        => new(path, true, true, ModificationBlocker.None, false, false, 10, null);
}
