using System.Reflection;

using BuildingBlocks.Application;

namespace ArchitectureTests;

/// <summary>
/// A query reads the reader endpoint unless it says otherwise (lesson s3-l02). A query handler may
/// depend only on read ports (interfaces named *Reads) and on other modules' contracts, which are
/// read-side too; a handler that must see the latest write says so with [ReadsTheWriter].
/// </summary>
public class ReadSideRulesTests
{
    private static readonly Type[] QueryHandlers = [.. Directory.GetFiles(AppContext.BaseDirectory, "*.Application.dll")
        .Select(f => Assembly.Load(Path.GetFileNameWithoutExtension(f)))
        .Where(a => a.GetName().Name != "BuildingBlocks.Application")
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(IsQueryHandler))];

    public static TheoryData<string> Handlers() => [.. QueryHandlers.Select(t => t.FullName!)];

    [Fact]
    public void There_are_query_handlers_to_check() =>
        Assert.Contains(QueryHandlers, t => t.Name == "GetCampaignHandler");

    [Theory]
    [MemberData(nameof(Handlers))]
    public void A_query_handler_reads_the_reader_unless_it_is_marked(string handler)
    {
        var type = QueryHandlers.Single(t => t.FullName == handler);
        if (type.GetCustomAttribute<ReadsTheWriterAttribute>() is not null)
        {
            return;
        }

        var dependencies = type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);
        Assert.All(dependencies, d => Assert.True(
            d.Name.EndsWith("Reads", StringComparison.Ordinal) || d.Assembly.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal),
            $"{type.Name} depends on {d.Name}, a writer-side port; read through a *Reads port or mark it [ReadsTheWriter]."));
    }

    private static bool IsQueryHandler(Type i) => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>);
}
