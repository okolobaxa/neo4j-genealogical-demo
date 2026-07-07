namespace Neo4gen.Server.Cypher;

public static class CypherEndpoints
{
    public static RouteGroupBuilder MapCypherEndpoints(this RouteGroupBuilder api)
    {
        // POST /api/cypher/rewrite — rewrites an aggregating query into a graph-returning one via
        // OpenAI, so the frontend's "Visualise" button gets a query that renders nodes/edges.
        api.MapPost("cypher/rewrite", async (CypherRewriter rewriter, CypherRewriteRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Cypher))
            {
                return Results.BadRequest("A non-empty 'cypher' query is required.");
            }

            var query = await rewriter.RewriteAsync(request.Cypher, ct);
            return Results.Ok(new CypherRewriteResponse(query));
        })
        .WithName("RewriteCypher");

        return api;
    }
}

// Body of POST /api/cypher/rewrite — the original (possibly aggregating) Cypher.
public record CypherRewriteRequest(string Cypher);

// The graph-returning rewrite the frontend should run.
public record CypherRewriteResponse(string Query);
