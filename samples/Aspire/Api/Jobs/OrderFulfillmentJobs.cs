using Immediate.Handlers.Shared;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Jobs;

[Handler, Job("order-received")]
public sealed partial class ReceiveOrderJob(ILogger<ReceiveOrderJob> logger)
{
	public sealed record Payload(Guid OrderId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		OrderWorkflowStep.RunAsync(logger, payload.OrderId, "received", cancellationToken);
}

[Handler, Job("order-reserve-inventory")]
public sealed partial class ReserveInventoryJob(ILogger<ReserveInventoryJob> logger)
{
	public sealed record Payload(Guid OrderId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		OrderWorkflowStep.RunAsync(logger, payload.OrderId, "inventory reserved", cancellationToken);
}

[Handler, Job("order-fraud-check")]
public sealed partial class FraudCheckJob(
	ILogger<FraudCheckJob> logger,
	RecordFraudAssessmentJob.Scheduler recordFraudAssessment
)
{
	public sealed record Payload(Guid OrderId) : IJobRequest
	{
		public JobDetails? JobDetails { get; set; }
	}

	private async ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken)
	{
		await OrderWorkflowStep.RunAsync(
			logger,
			payload.OrderId,
			"fraud check passed",
			cancellationToken
		);

		var currentJob = payload.JobDetails
			?? throw new InvalidOperationException("Job details were not populated.");
		_ = recordFraudAssessment.ScheduleAfter(
			currentJob,
			new(payload.OrderId),
			ContinuationOptions.BeforeContinuations
		);
	}
}

[Handler, Job("order-record-fraud-assessment")]
public sealed partial class RecordFraudAssessmentJob(ILogger<RecordFraudAssessmentJob> logger)
{
	public sealed record Payload(Guid OrderId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		OrderWorkflowStep.RunAsync(
			logger,
			payload.OrderId,
			"fraud assessment recorded by a dynamic continuation",
			cancellationToken
		);
}

[Handler, Job("order-capture-payment")]
public sealed partial class CapturePaymentJob(ILogger<CapturePaymentJob> logger)
{
	public sealed record Payload(Guid OrderId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		OrderWorkflowStep.RunAsync(logger, payload.OrderId, "payment captured", cancellationToken);
}

[Handler, Job("order-prepare-fulfillment")]
public sealed partial class PrepareFulfillmentJob(ILogger<PrepareFulfillmentJob> logger)
{
	public sealed record Payload(Guid OrderId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		OrderWorkflowStep.RunAsync(logger, payload.OrderId, "fulfillment prepared", cancellationToken);
}

[Handler, Job("order-create-label")]
public sealed partial class CreateShippingLabelJob(ILogger<CreateShippingLabelJob> logger)
{
	public sealed record Payload(Guid OrderId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		OrderWorkflowStep.RunAsync(logger, payload.OrderId, "shipping label created", cancellationToken);
}

[Handler, Job("order-pack")]
public sealed partial class PackOrderJob(ILogger<PackOrderJob> logger)
{
	public sealed record Payload(Guid OrderId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		OrderWorkflowStep.RunAsync(logger, payload.OrderId, "packed", cancellationToken);
}

[Handler, Job("order-dispatch")]
public sealed partial class DispatchOrderJob(ILogger<DispatchOrderJob> logger)
{
	public sealed record Payload(Guid OrderId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		OrderWorkflowStep.RunAsync(logger, payload.OrderId, "dispatched", cancellationToken);
}

[Handler, Job("order-notify-customer")]
public sealed partial class NotifyCustomerJob(ILogger<NotifyCustomerJob> logger)
{
	public sealed record Payload(Guid OrderId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		OrderWorkflowStep.RunAsync(logger, payload.OrderId, "customer notified", cancellationToken);
}

[Handler, Job("order-write-audit")]
public sealed partial class WriteOrderAuditJob(ILogger<WriteOrderAuditJob> logger)
{
	public sealed record Payload(Guid OrderId);

	private ValueTask HandleAsync(Payload payload, CancellationToken cancellationToken) =>
		OrderWorkflowStep.RunAsync(logger, payload.OrderId, "audit record written", cancellationToken);
}

internal static class OrderWorkflowStep
{
	public static async ValueTask RunAsync(
		ILogger logger,
		Guid orderId,
		string step,
		CancellationToken cancellationToken
	)
	{
		logger.LogInformation("Order {OrderId}: {WorkflowStep}", orderId, step);
		await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
	}
}
