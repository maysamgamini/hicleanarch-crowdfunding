
using Campaigns.Application;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Campaigns.Infrastructure;

/// <summary>
/// When the campaign reply's totals fields were deprecated, and when they go (lesson s5-l06). They
/// are removed before Campaigns runs in its own host (lesson s5-l08); the Sunset date must not be
/// earlier than that release.
/// </summary>
public sealed class ReplyTotalsOptions
{
    public DateTimeOffset DeprecatedAt { get; set; } = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset SunsetAt { get; set; } = new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// <c>pledged_minor</c> and <c>backers</c> in <c>GET /v1/campaigns/{id}</c> go through parallel change:
/// still filled in process, marked deprecated in the OpenAPI document, and every reply says so
/// (Deprecation, RFC 9745; Sunset, RFC 8594) with a link to where the totals live:
/// <c>GET /v1/campaigns/{id}/totals</c>, behind the CDN.
/// </summary>
internal static class ReplyTotalsDeprecation
{
    public static readonly IReadOnlyList<string> Fields = ["pledged_minor", "backers"];

    public static IServiceCollection AddReplyTotalsDeprecation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ReplyTotalsOptions>().Bind(configuration.GetSection("Campaigns:ReplyTotals"))
            .Validate(o => o.SunsetAt > o.DeprecatedAt, "Campaigns:ReplyTotals: the Sunset date must come after the deprecation.")
            .ValidateOnStart();
        services.ConfigureAll<OpenApiOptions>(o => o.AddSchemaTransformer<DeprecatedReplyTotals>());
        return services;
    }

    public static void Mark(HttpContext http, string campaignId)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<ReplyTotalsOptions>>().Value;
        Deprecations.Mark(http, options.DeprecatedAt, options.SunsetAt, $"/v1/campaigns/{campaignId}/totals");
    }
}

/// <summary>Marks the two fields deprecated in the campaign reply's schema.</summary>
internal sealed class DeprecatedReplyTotals : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(CampaignView) && schema.Properties is { } properties)
        {
            foreach (var field in ReplyTotalsDeprecation.Fields)
            {
                if (properties.TryGetValue(field, out var property) && property is OpenApiSchema inline)
                {
                    inline.Deprecated = true;
                }
            }
        }

        return Task.CompletedTask;
    }
}
