using System.Runtime.CompilerServices;

// The integration tests open the module's own transaction to prove what it does and does not commit.
[assembly: InternalsVisibleTo("IntegrationTests")]
