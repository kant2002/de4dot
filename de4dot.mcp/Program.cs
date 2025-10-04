using de4dot.mcp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer()
	.WithHttpTransport()
	.WithTools<ObfuscatorTool>();
builder.Services.AddSingleton<DeobfuscationSessionManager>();

var app = builder.Build();
app.UseHttpsRedirection();
app.MapMcp();

await app.RunAsync();
