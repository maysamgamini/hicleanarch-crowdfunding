namespace BuildingBlocks.Application;

/// <summary>
/// Marks a query handler that must see the latest write, such as a backer's own pledge right after
/// they made it. Every other query handler reads through read ports, which go to the database's
/// reader endpoint and may be a moment behind.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ReadsTheWriterAttribute : Attribute;
