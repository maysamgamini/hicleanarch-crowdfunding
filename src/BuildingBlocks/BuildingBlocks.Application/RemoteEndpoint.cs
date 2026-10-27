namespace BuildingBlocks.Application;

/// <summary>
/// Marks a command that another service may send over HTTP: the method and path of the endpoint that
/// carries it. The command is the request body and its result the reply, so both are part of the
/// module's published contract, and a breaking change to either takes a new path.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RemoteEndpointAttribute(string method, string path) : Attribute
{
    public string Method { get; } = method;

    public string Path { get; } = path;
}
