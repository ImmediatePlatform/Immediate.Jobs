using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Validations.Shared;

[assembly: Behaviors(typeof(ValidationBehavior<,>))]

namespace Immediate.Jobs.Dashboard.Endpoints;

[RouteGroup("api")]
internal sealed partial class DashboardApi;
