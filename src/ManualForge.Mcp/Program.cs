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

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "manualforge", Version = "0.1.0" };

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
            "report what it appears to say rather than asserting it.";
    })
    .WithStdioServerTransport()
    .WithTools<ManualTools>();

await builder.Build().RunAsync();
return 0;
