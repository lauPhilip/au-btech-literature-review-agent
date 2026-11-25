# Mcp

The MCP server, so other assistants can run a literature review: the Model Context Protocol over Streamable HTTP at `/mcp`, built on the official C# SDK (`ModelContextProtocol.AspNetCore`). It sits behind the same `Api:Enabled` switch as the REST API, so it is off in production until that is set.

`ReviewMcpTools.cs` holds the tools: `start_literature_review`, `get_review_status`, `get_review_report`, `get_review_file` and `cancel_review`. Each one calls `ReviewApi` in `Api/`, the same methods the REST API calls, so there is no second copy of the checks, the limits or the key handling. The caller's MCP client sends its own Mistral key in the `X-Mistral-Key` header, as for the API. `McpSetup.cs` registers the server (stateless, so nothing is kept between calls) and maps it, and keeps its answers uncompressed because a start carries the edit key. The SDK is 2.x, which answers clients on both the 2025-11-25 protocol (with its `initialize` handshake) and the 2026-07-28 protocol (no handshake); stateless is its default, and `McpSetup` still sets it so the choice is visible.

The developer wiki shows a client configuration and a "/literature-review" command: [9. The API and the MCP server](../../docs/wiki/09-api-and-mcp.md).
