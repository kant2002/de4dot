using System.ComponentModel;
using de4dot.mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer()
	.WithHttpTransport()
	.WithTools<ObfuscatorTool>()
	.WithResources<ObfuscatedFileResource>();
builder.Services.AddSingleton<DeobfuscationSessionManager>();

var app = builder.Build();
app.UseHttpsRedirection();
app.MapMcp();

await app.RunAsync();

[McpServerResourceType]
class ObfuscatedFileResource {

	[McpServerResource(UriTemplate = "de4dot://files/{id}", Name = "Obfuscated file")]
	//[Description("The obfuscated file")]
	public static ReadResourceResult TemplateResource(RequestContext<ReadResourceRequestParams> requestContext, string id) {
		if (requestContext.Services is null) {
			throw new NotSupportedException($"Unknown resource: {requestContext.Params?.Uri}");
		}

		var manager = requestContext.Services.GetRequiredService<DeobfuscationSessionManager>();
		var sessionId = requestContext.Server.SessionId!;
		var session = manager.GetSession(sessionId);
		if (session.File is null) {
			throw new NotSupportedException($"Unknown resource: {requestContext.Params?.Uri}");
		}

		var file = session.File;
		var memoryStream = new MemoryStream();
		file.Save(memoryStream);
		memoryStream.Position = 0;

		return new() {
			Contents = [new BlobResourceContents {
				Blob = Convert.ToBase64String(memoryStream.ToArray()),
				MimeType = "application/octet-stream",
				Uri = $"de4dot://files/{id}",
			}]
		};
	}
}
