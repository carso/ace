// ACE MCP Server — stdio Model Context Protocol host (SRS §6.2, §8).
//
// CRITICAL INVARIANT: stdout is reserved for MCP JSON-RPC framing. Any write to
// stdout that is not protocol framing corrupts the transport. All diagnostics and
// console logging therefore go to stderr only (LogToStandardErrorThreshold = Trace).
// Never use Console.WriteLine / Console.Out anywhere in this process.

using Ace.Core.Platform;
using Ace.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Route every console log record to stderr so stdout stays a clean MCP channel.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// ACE Core services. The facade is the single seam: it lazily loads/indexes each
// repository on first tool call and caches the session (§4.5, §17).
builder.Services.AddSingleton<IFileSystemService, FileSystemService>();
builder.Services.AddSingleton<AceEngineFacade>();

// MCP server: stdio transport + tool discovery over [McpServerToolType] classes.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
