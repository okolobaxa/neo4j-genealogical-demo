using Neo4j.Driver;

namespace Neo4gen.Server.Graph;

// Neo4j graph endpoint: runs Cypher via the official .NET driver (Bolt) and shapes the
// result into the node/relationship format the frontend's NVL visualization expects.
public static class GraphEndpoints
{
    // Registers the Neo4j driver as a singleton when a connection string is present.
    // Leave ConnectionStrings:Neo4j unset (e.g. running without a database) and the
    // /api/graph endpoint reports "not configured" instead of the app failing to start.
    public static IServiceCollection AddNeo4j(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Neo4j");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var conn = Neo4jConnection.Parse(connectionString);
            services.AddSingleton(conn);
            services.AddSingleton(GraphDatabase.Driver(conn.Uri, conn.AuthToken));
        }

        return services;
    }

    public static RouteGroupBuilder MapGraphEndpoints(this RouteGroupBuilder api)
    {
        // GET /api/graph?limit=500 — returns nodes + relationships for the visualization.
        api.MapGet("graph", async (IDriver? driver, Neo4jConnection? conn, int? limit, CancellationToken ct) =>
        {
            if (driver is null || conn is null)
            {
                return Results.Problem(
                    "Neo4j is not configured. Set the ConnectionStrings:Neo4j connection string.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var database = conn.Database;
            const string cypher = """
                MATCH (n)
                WHERE NOT n.name STARTS WITH '__'
                MATCH (n)-[r]->(m)
                WHERE NOT m.name STARTS WITH '__'
                RETURN n, r, m
                ORDER BY rand()
                LIMIT $limit
                """;

            var result = await driver
                .ExecutableQuery(cypher)
                .WithParameters(new { limit = limit ?? 500 })
                .WithConfig(new QueryConfig(routing: RoutingControl.Readers, database: database))
                .ExecuteAsync(ct);

            // Dedupe by element id — the same node/rel can appear across many rows.
            var nodes = new Dictionary<string, GraphNode>();
            var rels = new Dictionary<string, GraphRel>();

            foreach (var record in result.Result)
            {
                AddNode(nodes, record["n"].As<INode>());
                AddNode(nodes, record["m"].As<INode>());

                var r = record["r"].As<IRelationship>();
                rels[r.ElementId] = new GraphRel(r.ElementId, r.StartNodeElementId, r.EndNodeElementId, r.Type);
            }

            return Results.Ok(new GraphResponse([.. nodes.Values], [.. rels.Values]));
        })
        .WithName("GetGraph");

        // POST /api/graph/query — runs a caller-supplied Cypher query (e.g. one the agent
        // suggested in chat) and shapes whatever nodes/relationships it returns for the
        // visualization. Executed with reader routing so writes are rejected on a cluster.
        api.MapPost("graph/query", async (IDriver? driver, Neo4jConnection? conn, GraphQueryRequest request, CancellationToken ct) =>
        {
            if (driver is null || conn is null)
            {
                return Results.Problem(
                    "Neo4j is not configured. Set the ConnectionStrings:Neo4j connection string.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(request.Cypher))
            {
                return Results.BadRequest("A non-empty 'cypher' query is required.");
            }

            IReadOnlyList<IRecord> records;
            try
            {
                var result = await driver
                    .ExecutableQuery(request.Cypher)
                    .WithConfig(new QueryConfig(routing: RoutingControl.Readers, database: conn.Database))
                    .ExecuteAsync(ct);
                records = result.Result;
            }
            catch (Neo4jException ex)
            {
                // Surface Cypher/syntax errors to the UI instead of a 500.
                return Results.BadRequest(ex.Message);
            }

            // Arbitrary queries return arbitrary columns, so walk every value in every record and
            // pull out any nodes, relationships, and paths (including inside lists).
            var nodes = new Dictionary<string, GraphNode>();
            var rels = new Dictionary<string, GraphRel>();
            foreach (var record in records)
            {
                foreach (var value in record.Values.Values)
                {
                    Collect(value, nodes, rels);
                }
            }

            // NVL needs both endpoints of every relationship present, so drop any dangling rels
            // (e.g. a query that returned a relationship without its start/end nodes).
            var kept = rels.Values.Where(r => nodes.ContainsKey(r.From) && nodes.ContainsKey(r.To));

            return Results.Ok(new GraphResponse([.. nodes.Values], [.. kept]));
        })
        .WithName("RunGraphQuery");

        // POST /api/graph/merge — combines two nodes into one via APOC's mergeNodes, moving all
        // relationships and properties onto the surviving (first) node. This is a write, so it runs
        // with writer routing (unlike /graph/query, which is read-only). Requires the APOC plugin.
        api.MapPost("graph/merge", async (IDriver? driver, Neo4jConnection? conn, MergeNodesRequest request, CancellationToken ct) =>
        {
            if (driver is null || conn is null)
            {
                return Results.Problem(
                    "Neo4j is not configured. Set the ConnectionStrings:Neo4j connection string.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(request.A) || string.IsNullOrWhiteSpace(request.B))
            {
                return Results.BadRequest("Two node ids ('a' and 'b') are required.");
            }

            if (request.A == request.B)
            {
                return Results.BadRequest("Cannot merge a node with itself.");
            }

            // mergeNodes keeps the first node ($a) and folds the second ($b) into it:
            //   mergeRels: true       -> all of $b's relationships are reattached to $a
            //   properties: 'discard' -> $a keeps its own property values on any conflict
            const string cypher = """
                MATCH (a) WHERE elementId(a) = $a
                MATCH (b) WHERE elementId(b) = $b
                CALL apoc.refactor.mergeNodes([a, b], {properties: 'discard', mergeRels: true})
                YIELD node
                RETURN elementId(node) AS id
                """;

            try
            {
                var result = await driver
                    .ExecutableQuery(cypher)
                    .WithParameters(new { a = request.A, b = request.B })
                    .WithConfig(new QueryConfig(routing: RoutingControl.Writers, database: conn.Database))
                    .ExecuteAsync(ct);

                var record = result.Result.FirstOrDefault();
                if (record is null)
                {
                    return Results.BadRequest("Merge returned no node — check that both selected nodes still exist.");
                }

                return Results.Ok(new MergeNodesResponse(record["id"].As<string>()));
            }
            catch (Neo4jException ex)
            {
                // Surface APOC-not-installed / permission / syntax errors to the UI instead of a 500.
                return Results.BadRequest(ex.Message);
            }
        })
        .WithName("MergeNodes");

        // POST /api/graph/delete — removes one or more nodes by element id. DETACH DELETE drops each
        // node's relationships first so the delete can't fail on a still-connected node. Writer routing.
        api.MapPost("graph/delete", async (IDriver? driver, Neo4jConnection? conn, DeleteNodesRequest request, CancellationToken ct) =>
        {
            if (driver is null || conn is null)
            {
                return Results.Problem(
                    "Neo4j is not configured. Set the ConnectionStrings:Neo4j connection string.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var ids = request.Ids?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToArray() ?? [];

            if (ids.Length == 0)
            {
                return Results.BadRequest("At least one node id is required.");
            }

            const string cypher = """
                MATCH (n) WHERE elementId(n) IN $ids
                DETACH DELETE n
                """;

            try
            {
                var result = await driver
                    .ExecutableQuery(cypher)
                    .WithParameters(new { ids })
                    .WithConfig(new QueryConfig(routing: RoutingControl.Writers, database: conn.Database))
                    .ExecuteAsync(ct);

                return Results.Ok(new DeleteNodesResponse(result.Summary.Counters.NodesDeleted));
            }
            catch (Neo4jException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
        .WithName("DeleteNodes");

        return api;
    }

    // Recursively pulls nodes/relationships out of a Cypher return value of any shape.
    private static void Collect(object? value, Dictionary<string, GraphNode> nodes, Dictionary<string, GraphRel> rels)
    {
        switch (value)
        {
            case INode node:
                AddNode(nodes, node);
                break;
            case IRelationship rel:
                rels[rel.ElementId] = new GraphRel(rel.ElementId, rel.StartNodeElementId, rel.EndNodeElementId, rel.Type);
                break;
            case IPath path:
                foreach (var n in path.Nodes) AddNode(nodes, n);
                foreach (var r in path.Relationships)
                    rels[r.ElementId] = new GraphRel(r.ElementId, r.StartNodeElementId, r.EndNodeElementId, r.Type);
                break;
            case System.Collections.IEnumerable list and not string:
                foreach (var item in list) Collect(item, nodes, rels);
                break;
        }
    }

    private static void AddNode(Dictionary<string, GraphNode> nodes, INode node)
    {
        if (nodes.ContainsKey(node.ElementId))
        {
            return;
        }

        var label = node.Labels.FirstOrDefault() ?? "Node";
        // Prefer a human-friendly property for the caption, falling back to the label.
        var caption =
            TryProp(node, "name") ??
            TryProp(node, "title") ??
            label;

        var properties = node.Properties.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "");

        nodes[node.ElementId] = new GraphNode(node.ElementId, caption, label, properties);
    }

    private static string? TryProp(INode node, string key) =>
        node.Properties.TryGetValue(key, out var value) ? value?.ToString() : null;
}

// Body of POST /api/graph/query — the Cypher to run for the visualization.
public record GraphQueryRequest(string Cypher);

// Body/response of POST /api/graph/merge — the element ids of the two nodes to combine, and the
// element id of the surviving merged node.
public record MergeNodesRequest(string A, string B);
public record MergeNodesResponse(string NodeId);

// Body/response of POST /api/graph/delete — element ids of the nodes to remove, and how many
// nodes were actually deleted.
public record DeleteNodesRequest(IReadOnlyList<string> Ids);
public record DeleteNodesResponse(int NodesDeleted);

// Shapes match the NVL Node/Relationship types on the frontend (serialized camelCase).
public record GraphNode(string Id, string Caption, string Label, IReadOnlyDictionary<string, string> Properties);
public record GraphRel(string Id, string From, string To, string Caption);
public record GraphResponse(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphRel> Relationships);
