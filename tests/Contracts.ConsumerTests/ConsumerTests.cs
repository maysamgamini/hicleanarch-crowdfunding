using System.Text.Json;
using System.Text.Json.Nodes;

using BuildingBlocks.Application;

namespace Contracts.ConsumerTests;

/// <summary>
/// Each consumer reads the producer's published examples with the options it reads real messages with
/// (<see cref="ContractJson.Options"/>, which the dispatcher uses): an allowed example is accepted, a
/// field it does not know is ignored, and an example that lacks a required field, or carries null in
/// one that is not nullable, is refused.
/// </summary>
public class ConsumerTests
{
    public static TheoryData<string, string, string> Examples()
    {
        var rows = new TheoryData<string, string, string>();
        foreach (var (consumer, type) in Published.Consumed)
        {
            foreach (var file in Published.ExamplesOf(Published.ProducerOf(type), type.Name))
            {
                rows.Add(consumer, Published.ProducerOf(type), file);
            }
        }

        // The remote endpoint: Payments reads the request, the caller reads the reply.
        rows.Add("Payments", "Payments", "StartCardSetup.request.json");
        rows.Add("the caller", "Payments", "StartCardSetup.response.json");
        return rows;
    }

    [Fact]
    public void Every_event_a_module_reads_from_another_has_a_published_example() =>
        Assert.All(Published.Consumed, c =>
            Assert.True(Published.ExamplesOf(Published.ProducerOf(c.Event), c.Event.Name).Any(),
                $"{c.Consumer} reads {c.Event.Name}, and {Published.ProducerOf(c.Event)}.Contracts publishes no example of it."));

    [Fact]
    public void The_consumers_found_include_the_payments_events_pledges_reads() =>
        Assert.Superset(
            new HashSet<string> { "Pledges:PaymentMethodSaved", "Pledges:PledgeChargeSucceeded", "Pledges:PledgeChargeFailed", "Payments:PledgeChargeRequested", "Payments:CampaignFundingDecided", "Campaigns:CampaignSettled" },
            Published.Consumed.Select(c => $"{c.Consumer}:{c.Event.Name}").ToHashSet());

    [Theory]
    [MemberData(nameof(Examples))]
    public void A_published_example_is_accepted(string consumer, string producer, string file) =>
        Assert.True(Read(producer, file, Published.Example(producer, file)) is not null, $"{consumer} could not read {producer}'s {file}.");

    [Theory]
    [MemberData(nameof(Examples))]
    public void A_field_the_consumer_does_not_know_is_ignored(string consumer, string producer, string file)
    {
        var example = Published.Example(producer, file);
        var withMore = example.DeepClone().AsObject();
        withMore["addedInALaterMinorVersion"] = "EUR";
        withMore["anotherNewField"] = new JsonObject { ["nested"] = 1 };

        var read = Read(producer, file, withMore);

        Assert.True(Write(Read(producer, file, example)) == Write(read), $"{consumer} read {file} differently with fields it does not know.");
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void An_example_missing_a_required_field_is_refused(string consumer, string producer, string file)
    {
        var schema = Schema(producer, file);
        var required = schema["required"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.NotEmpty(required);

        Assert.All(required, field =>
        {
            var example = Published.Example(producer, file);
            example.Remove(field);
            var refused = Record.Exception(() => Read(producer, file, example));
            Assert.True(refused is JsonException && refused.Message.Contains(field, StringComparison.Ordinal), $"{consumer} accepted {file} without {field}.");
        });
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void An_example_with_null_in_a_required_field_that_is_not_nullable_is_refused(string consumer, string producer, string file)
    {
        var schema = Schema(producer, file);
        var notNullable = schema["required"]!.AsArray().Select(n => (string)n!)
            .Where(f => schema["properties"]![f]!["type"] switch
            {
                JsonArray types => types.All(t => (string)t! != "null"),
                var type => (string?)type != "null",
            })
            .ToList();
        Assert.NotEmpty(notNullable);

        Assert.All(notNullable, field =>
        {
            var example = Published.Example(producer, file);
            example[field] = null;
            Assert.True(Record.Exception(() => Read(producer, file, example)) is JsonException, $"{consumer} accepted {file} with {field} null.");
        });
    }

    private static object Read(string producer, string file, JsonObject json) =>
        JsonSerializer.Deserialize(json.ToJsonString(), TypeOf(producer, file), ContractJson.Options)!;

    private static string Write(object message) => JsonSerializer.Serialize(message, message.GetType(), ContractJson.Options);

    private static Type TypeOf(string producer, string file)
    {
        var parts = file.Split('.');
        return parts[1] switch
        {
            "request" => Published.Type(producer, parts[0]),
            "response" => Published.Type(producer, parts[0]).GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)).GetGenericArguments()[0],
            _ => Published.Type(producer, parts[0]),
        };
    }

    private static JsonObject Schema(string producer, string file)
    {
        var parts = file.Split('.');
        var contract = Published.Contract(producer);
        return parts[1] is "request" or "response"
            ? contract["endpoints"]!.AsObject().Select(e => e.Value!).Single(e => (string)e["command"]! == parts[0])[parts[1]]!.AsObject()
            : contract["events"]![parts[0]]!["schema"]!.AsObject();
    }
}
