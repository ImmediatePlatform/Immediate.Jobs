using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Validations.Shared;

#pragma warning disable CA1812 // Request types and route groups are activated by generated endpoints.

[assembly: Behaviors(typeof(ValidationBehavior<,>))]

namespace Immediate.Jobs.Dashboard;

[RouteGroup("api")]
internal sealed partial class DashboardApi;
