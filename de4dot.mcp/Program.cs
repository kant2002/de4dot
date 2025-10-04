using de4dot.mcp;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer()
	.WithStdioServerTransport()
	.WithTools<ObfuscatorTool>();
builder.Logging.AddConsole(consoleLogOptions => {
	// Configure all logs to go to stderr
	consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

var app = builder.Build();

await app.RunAsync();
