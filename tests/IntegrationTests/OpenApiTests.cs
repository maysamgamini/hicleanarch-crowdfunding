using System.Net.Http.Json;
using System.Text.Json;

namespace IntegrationTests;

/// <summary>The API's OpenAPI description, generated from the endpoints themselves.</summary>
public sealed class OpenApiTests(Database database)
{
    private static readonly string[] Operations = ["get", "put", "post", "delete", "patch", "head", "options", "trace"];

    [Fact]
    public async Task The_document_lists_every_mapped_route()
    {
        await using var host = database.StartHost();
        var mapped = MappedEndpoints.Of(host).Select(e => e.Name)
            // Not the API: the load balancer's checks and the document itself.
            .Where(name => !name.Contains(" /health", StringComparison.Ordinal) && !name.Contains(" /openapi/", StringComparison.Ordinal))
            // A group's own route, POST /v1/campaigns/, is listed without its trailing slash.
            .Select(name => name.TrimEnd('/'));

        var document = await host.CreateClient().GetFromJsonAsync<JsonElement>("/openapi/v1.json", TestContext.Current.CancellationToken);

        var listed = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => Operations.Contains(operation.Name))
                .Select(operation => $"{operation.Name.ToUpperInvariant()} {path.Name}"));
        Assert.Equal(mapped.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
    }
}
