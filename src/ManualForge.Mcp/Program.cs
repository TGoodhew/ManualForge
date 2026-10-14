using ManualForge.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// An MCP server speaks JSON-RPC on stdout, so anything else written there corrupts the protocol.
// Logging goes to stderr, which the host collects.
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var library = ManualLibraryContext.FromEnvironment()
              ?? (args.Length > 0 && Directory.Exists(args[0]) ? ManualLibraryContext.At(args[0]) : null);

if (library is null)
{
    await Console.Error.WriteLineAsync(
        "ManualForge MCP: set MANUALFORGE_LIBRARY to the folder of manuals, or pass it as the first " +
        "argument. Without it there is nothing to search and the server would offer tools that " +
        "cannot work.");
    return 1;
}

builder.Services.AddSingleton(library);

// Links to ManualForge's view of a page, so a citation opens the page it cites (#79). Registered
// before the MCP server, so it is answering, or knows it cannot, before the first tool call.
var port = PageLinks.ConfiguredPort();
builder.Services.AddSingleton(new PageLinks(port ?? PageLinks.DefaultPort));
if (port is not null)
    builder.Services.AddHostedService<PageServer>();

builder.Services
    .AddMcpServer(options =>
    {
        // The build's own version and commit, so a client's log says which build answered.
        options.ServerInfo = new() { Name = "manualforge", Version = ManualForge.Core.Diagnostics.BuildStamp.Version };

        // Read by the client before any tool is called, and the place to say how this server relates
        // to the other one that may be looking at the same folder.
        options.ServerInstructions =
            "manualforge searches a local library of scanned technical manuals by full text. Every " +
            "page of every manual is indexed, so it answers 'where is this discussed at all' rather " +
            "than 'what does the manual for model X say'.\n\n" +
            "If gpib-mcp is also present, the two are complementary and neither replaces the other. " +
            "Prefer gpib-mcp's manual_search when you know the instrument model: it narrows by model " +
            "and returns longer passages. Prefer library_search here when you do not, or when that " +
            "found nothing - it ranks across every page rather than the best dozen filenames, which " +
            "is exactly the case a filename cannot help with.\n\n" +
            "Always read the page with read_manual_page before answering, and cite the manual and " +
            "page. Many of these manuals are OCR'd scans, so the text carries recognition errors: " +
            "report what it appears to say rather than asserting it.\n\n" +
            "When a result gives a link for a page, make each citation of that page a markdown link " +
            "to it, e.g. [432A-OSM.pdf, p. 23](link). The link opens ManualForge's view of the page " +
            "on the user's own PC, so they can check what you cite against the page itself. Use the " +
            "link exactly as given; never make one up. " + ManualTools.SidePanelAdvice;
    })
    .WithStdioServerTransport()
    .WithTools<ManualTools>();

await builder.Build().RunAsync();
return 0;
