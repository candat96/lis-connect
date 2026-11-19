namespace Lis.Server.AgentApi;

/// <summary><c>GET /agent/v1/channel</c>: kênh lệnh WebSocket. Nằm trong nhóm có bộ lọc khoá nên khoá sai / thu hồi bị 401 trước khi nâng cấp.</summary>
public static class AgentChannelEndpoint
{
    public static void MapAgentChannel(this RouteGroupBuilder group) =>
        group.MapGet("/channel", async (HttpContext http, AgentChannelHub hub) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.Problem("Cần kết nối WebSocket", statusCode: StatusCodes.Status400BadRequest);
            }

            var identity = AgentEndpoints.Identity(http);
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(identity, socket, http.RequestAborted);
            return Results.Empty;
        });
}
