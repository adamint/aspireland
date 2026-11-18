using MetroOps.Contracts;
using MetroOps.PublicApi;
using MetroOps.ServiceDefaults;

namespace MetroOps.OperationsApi;

public static class OperationsRoutes
{
    public static void Map(WebApplication app)
    {
        PublicApiRoutes.Map(app);
        app.MapGet("/api/capabilities", (HttpContext context, IHttpClientFactory clients, IConfiguration config) =>
            ForwardOperator(context, clients, config, "/operator/capabilities"));
        app.MapPost("/api/assistant", (HttpContext context, IHttpClientFactory clients, IConfiguration config) =>
            ForwardOperator(context, clients, config, "/operator/assistant"));
        app.MapGet("/api/world", async (ServiceClients clients, CancellationToken ct) =>
            new WorldSnapshot(await clients.Get<WorldState>("scenario", "/state", ct), Network.Definition));

        app.MapPost("/api/clock", async (ClockRequest request, HttpContext context, ServiceClients clients, IConfiguration config, CancellationToken ct) =>
            !Authorized(context, config) ? Results.Unauthorized() : Results.Ok(await clients.Post<object>("scenario", "/clock", request, ct, config["SERVICE_KEY"])));
        app.MapPost("/api/authority", async (AuthorityRequest request, HttpContext context, ServiceClients clients, IConfiguration config, CancellationToken ct) =>
            !Authorized(context, config) ? Results.Unauthorized() : Results.Ok(await clients.Post<object>("scenario", "/authority", request, ct, config["SERVICE_KEY"])));
        app.MapPost("/api/scenarios", async (ScenarioRequest request, HttpContext context, ServiceClients clients, IConfiguration config, CancellationToken ct) =>
            !Authorized(context, config) ? Results.Unauthorized() : Results.Ok(await clients.Post<object>("scenario", "/scenarios", request, ct, config["SERVICE_KEY"])));
        app.MapPost("/api/actions", async (ActionRequest request, HttpContext context, ServiceClients clients, IConfiguration config, CancellationToken ct) =>
        {
            if (!Authorized(context, config)) return Results.Unauthorized();
            var service = request.Action.Kind is "incident" or "clear-incident" ? "incidents" : "dispatch";
            var command = new CommandEnvelope(request.CommandId ?? Guid.NewGuid().ToString("N"), request.ScenarioId, request.Generation,
                request.ExpectedVersion, "operator", request.Reason, request.Action);
            return Results.Ok(await clients.Post<CommandResult>(service, "/actions", command, ct, config["SERVICE_KEY"]));
        });
        app.MapPost("/api/compare", async (CompareRequest request, HttpContext context, ServiceClients clients, IConfiguration config, CancellationToken ct) =>
            !Authorized(context, config) ? Results.Unauthorized() : Results.Ok(await clients.Post<ComparisonResult>("scenario", "/compare", request, ct)));
        app.MapPost("/api/proposals/{id}/approve", async (string id, ApprovalRequest request, HttpContext context, ServiceClients clients, IConfiguration config, CancellationToken ct) =>
        {
            if (!Authorized(context, config)) return Results.Unauthorized();
            var proposal = await clients.Get<ActionProposal>("ai-operations", $"/operator/proposals/{Uri.EscapeDataString(id)}", ct,
                config["OPERATOR_KEY"], "X-Operator-Key");
            if (request.ScenarioId != proposal.ScenarioId || request.Generation != proposal.Generation)
                return Results.Conflict(new { error = "The proposal belongs to another scenario." });
            var command = new CommandEnvelope($"approval:{proposal.Id}", proposal.ScenarioId, proposal.Generation, proposal.Version,
                "operator-approved-agent", proposal.Explanation, proposal.Action);
            return Results.Ok(await clients.Post<CommandResult>("dispatch", "/approved-actions", command, ct, config["SERVICE_KEY"]));
        });
    }

    private static async Task ForwardOperator(HttpContext context, IHttpClientFactory clients, IConfiguration config, string path)
    {
        if (!Authorized(context, config))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await AiForwarder.Forward(context, clients, path, config["OPERATOR_KEY"]);
    }

    private static bool Authorized(HttpContext context, IConfiguration config) => ServiceKey.Matches(context, config, "OPERATOR_KEY", "X-Operator-Key");
    public sealed record ApprovalRequest(string ScenarioId, int Generation);
}
