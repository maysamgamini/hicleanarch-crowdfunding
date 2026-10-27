using CrowdFunding.Messaging;

namespace Messaging.Tests;

public sealed class BrokerPlanTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(directory.FullName, "CrowdFunding.slnx")))
            {
                directory = directory.Parent ?? throw new InvalidOperationException("The repository root was not found.");
            }

            return directory.FullName;
        }
    }

    public static BrokerPlan Plan => BrokerPlan.Load(Path.Combine(AppContext.BaseDirectory, "events", "subscriptions.json"));

    [Fact]
    public void What_Terraform_reads_is_rendered_from_the_subscriptions_file()
    {
        var committed = Path.Combine(Root, "events", "broker-subscriptions.json");
        var rendered = Plan.Render();
        if (Environment.GetEnvironmentVariable("REGENERATE_BROKER_PLAN") == "1")
        {
            File.WriteAllText(committed, rendered);
        }

        Assert.True(File.Exists(committed) && File.ReadAllText(committed) == rendered,
            "events/broker-subscriptions.json is not what events/subscriptions.json implies; run REGENERATE_BROKER_PLAN=1 dotnet test tests/Messaging.Tests and commit it.");
    }

    [Fact]
    public void A_service_subscribes_only_to_the_events_of_other_services_that_its_modules_consume()
    {
        var subscriptions = Plan.Subscriptions.ToDictionary(s => (s.Topic, s.Queue), s => s.EventTypes);

        Assert.Equal(["PledgeChargeRequested"], subscriptions[("Pledges", "payments")].Where(t => t == "PledgeChargeRequested"));
        Assert.Contains("PaymentMethodSaved", subscriptions[("Payments", "monolith")]);
        Assert.DoesNotContain(("Payments", "payments"), subscriptions.Keys); // a service's own events stay in process
        Assert.DoesNotContain(("Pledges", "monolith"), subscriptions.Keys);
        Assert.DoesNotContain("PledgeActivated", subscriptions[("Pledges", "payments")]); // Payments does not consume it
    }
}
