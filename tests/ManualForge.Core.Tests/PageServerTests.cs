using System.Net;
using System.Net.Sockets;
using System.Text;
using ManualForge.Core.Auditing;
using ManualForge.Mcp;
using Xunit;

namespace ManualForge.Core.Tests;

/// <summary>
/// The page view a citation in Claude's answer opens (#79): what each link reaches, and what the
/// server refuses.
/// </summary>
public sealed class PageServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "manualforge-pages-" + Guid.NewGuid().ToString("N"));
    private readonly PageLinks _links = new(PageLinks.DefaultPort);

    public PageServerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "App Notes"));
        TestPdf.TypesetOnly(Path.Combine(_root, "App Notes", "an 69.pdf"), "Chopper stabilised amplifier.", pages: 3);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a manual");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ManualLibraryContext Library => ManualLibraryContext.At(_root);

    private PageServer Server() => new(Library, _links);

    private const string Host = "127.0.0.1:47332";

    private static string Path_(string link) => new Uri(link).AbsolutePath;

    [Theory]
    [InlineData("/manuals/a.pdf", "a.pdf", null, false)]
    [InlineData("/manuals/App%20Notes/an%2069.pdf/page/8", @"App Notes\an 69.pdf", 8, false)]
    [InlineData("/manuals/App%20Notes/an%2069.pdf/page/8.png", @"App Notes\an 69.pdf", 8, true)]
    public void ALinkSaysWhichManualAndPage(string path, string relative, int? page, bool picture)
    {
        Assert.True(PageLinks.TryParse(path, out var parsedRelative, out var parsedPage, out var parsedPicture));
        Assert.Equal(relative, parsedRelative);
        Assert.Equal(page, parsedPage);
        Assert.Equal(picture, parsedPicture);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/manuals/")]
    [InlineData("/manuals/notes.txt")]
    [InlineData("/manuals/a.pdf/page/0")]
    [InlineData("/manuals/a.pdf/page/-1")]
    [InlineData("/manuals/a.pdf/page/two")]
    [InlineData("/manuals//a.pdf")]
    [InlineData("/elsewhere/a.pdf")]
    public void AnythingElseIsNotALink(string path) => Assert.False(PageLinks.TryParse(path, out _, out _, out _));

    [Fact]
    public void ALinkCarriesThePathWithinTheLibraryAndNothingAboveIt()
    {
        var link = _links.ForPage(@"App Notes\an 69.pdf", 2);

        Assert.Equal("http://127.0.0.1:47332/manuals/App%20Notes/an%2069.pdf/page/2", link);
        Assert.True(PageLinks.TryParse(Path_(link), out var relative, out var page, out _));
        Assert.Equal(@"App Notes\an 69.pdf", relative);
        Assert.Equal(2, page);
    }

    [Fact]
    public async Task ThePageViewShowsThePageAndTheWayAround()
    {
        var answer = await Server().RespondAsync("GET", Host, Path_(_links.ForPage(@"App Notes\an 69.pdf", 2)));

        Assert.Equal(200, answer.Status);
        var html = Encoding.UTF8.GetString(answer.Body);
        Assert.Contains("Page 2 of 3", html, StringComparison.Ordinal);
        Assert.Contains("an 69.pdf", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"{_links.ForPage(@"App Notes\an 69.pdf", 1)}\" rel=\"prev\"", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"{_links.ForPage(@"App Notes\an 69.pdf", 3)}\" rel=\"next\"", html, StringComparison.Ordinal);
        Assert.Contains($"src=\"{_links.ForPicture(@"App Notes\an 69.pdf", 2)}\"", html, StringComparison.Ordinal);
        Assert.Contains("Chopper stabilised amplifier.", html, StringComparison.Ordinal);   // the PDF's own text
        Assert.DoesNotContain("Recovered by ManualForge", html, StringComparison.Ordinal); // nothing was
    }

    [Fact]
    public async Task TextTheRepairRecoveredIsShownAndMarked()
    {
        var pdf = Path.Combine(_root, "App Notes", "an 69.pdf");
        using (var store = new DoctorStore(DoctorStore.DefaultPathFor(_root)))
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pdf)));
            store.SaveRepair(new PageRepair(pdf, 2, hash, 300, "CHOPPER <AMPLIFIER>", 0.82, 2, DateTimeOffset.UtcNow));
        }

        var html = Encoding.UTF8.GetString((await Server().RespondAsync("GET", Host, Path_(_links.ForPage(@"App Notes\an 69.pdf", 2)))).Body);

        Assert.Contains("Recovered by ManualForge", html, StringComparison.Ordinal);
        Assert.Contains("82% mean confidence", html, StringComparison.Ordinal);
        Assert.Contains("CHOPPER &lt;AMPLIFIER&gt;", html, StringComparison.Ordinal);   // escaped, never markup
    }

    [Fact]
    public void ADrawingsLabelsAreFlowedButProseIsNot()
    {
        Assert.True(PageServer.IsLabels(string.Join('\n', "RANGE CHOPPER AMPLIFIER AMPL CALIBRATION FACTOR 5KHz MULTI- VIBRATOR LOW-PASS FILTER RECORDER".Split(' '))));
        Assert.False(PageServer.IsLabels(string.Join('\n', Enumerable.Repeat("The dc voltage is applied to the chopper op amp, which converts it to a square wave.", 14))));
        Assert.False(PageServer.IsLabels("CHOPPER\nAMPLIFIER"));   // too few to need columns
    }

    [Fact]
    public async Task ThePictureIsAPng()
    {
        var answer = await Server().RespondAsync("GET", Host, Path_(_links.ForPicture(@"App Notes\an 69.pdf", 3)));

        Assert.Equal(200, answer.Status);
        Assert.Equal("image/png", answer.ContentType);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], answer.Body.Take(4).ToArray());
    }

    [Fact]
    public async Task TheWholePdfIsTheFileItself()
    {
        var answer = await Server().RespondAsync("GET", Host, Path_(_links.ForDocument(@"App Notes\an 69.pdf")));

        Assert.Equal(200, answer.Status);
        Assert.Equal("application/pdf", answer.ContentType);
        Assert.Equal(Path.Combine(_root, "App Notes", "an 69.pdf"), answer.File);
    }

    [Fact]
    public async Task APageThatIsNotThereSaysHowManyThereAre()
    {
        var answer = await Server().RespondAsync("GET", Host, Path_(_links.ForPage(@"App Notes\an 69.pdf", 9)));

        Assert.Equal(404, answer.Status);
        Assert.Contains("has 3 pages", Encoding.UTF8.GetString(answer.Body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/manuals/..%5C..%5Csecrets.pdf")]
    [InlineData("/manuals/..%2F..%2Fsecrets.pdf")]
    [InlineData("/manuals/C:%5CWindows%5Csecrets.pdf")]
    [InlineData("/manuals/gone.pdf")]
    public async Task NothingOutsideTheLibraryIsServed(string path)
    {
        var answer = await Server().RespondAsync("GET", Host, path);

        Assert.Equal(404, answer.Status);
        Assert.Null(answer.File);
    }

    [Theory]
    [InlineData("attacker.example:47332")]
    [InlineData("127.0.0.1:80")]
    [InlineData(null)]
    public async Task OnlyRequestsAddressedTo127001AreAnswered(string? host)
    {
        // A web page that points a name of its own at 127.0.0.1 still sends that name.
        var answer = await Server().RespondAsync("GET", host, Path_(_links.ForDocument(@"App Notes\an 69.pdf")));

        Assert.Equal(403, answer.Status);
        Assert.Null(answer.File);
    }

    [Fact]
    public async Task OnlyReadingIsAllowed() =>
        Assert.Equal(405, (await Server().RespondAsync("POST", Host, Path_(_links.ForDocument(@"App Notes\an 69.pdf")))).Status);

    [Fact]
    public async Task ItAnswersOnTheLoopbackAndASecondServerUsesTheFirst()
    {
        var port = FreePort();
        var links = new PageLinks(port);
        using var first = new PageServer(Library, links);
        await first.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(links.Enabled);

            using var http = new HttpClient();
            var page = await http.GetAsync(new Uri(links.ForPage(@"App Notes\an 69.pdf", 1)));
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);

            // Claude Desktop and VS Code each start a server: the second finds the first serving the
            // same library and hands out its links.
            var same = new PageLinks(port);
            using (var second = new PageServer(Library, same))
            {
                await second.StartAsync(CancellationToken.None);
                Assert.True(same.Enabled);
            }

            // One for another library would hand out links to manuals the first does not have.
            var otherRoot = Path.Combine(_root, "App Notes");
            var other = new PageLinks(port);
            using (var third = new PageServer(ManualLibraryContext.At(otherRoot), other))
            {
                await third.StartAsync(CancellationToken.None);
                Assert.False(other.Enabled);
            }
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
