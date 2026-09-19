using Immediate.Jobs.Aspire.Api.Jobs;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Workflows;

public sealed class OrderFulfillmentWorkflow(
	BatchScheduler batches,
	ReceiveOrderJob.Scheduler receiveOrder,
	ReserveInventoryJob.Scheduler reserveInventory,
	FraudCheckJob.Scheduler fraudCheck,
	CapturePaymentJob.Scheduler capturePayment,
	PrepareFulfillmentJob.Scheduler prepareFulfillment,
	CreateShippingLabelJob.Scheduler createShippingLabel,
	PackOrderJob.Scheduler packOrder,
	DispatchOrderJob.Scheduler dispatchOrder,
	NotifyCustomerJob.Scheduler notifyCustomer,
	WriteOrderAuditJob.Scheduler writeAudit
)
{
	public const int InitialJobCount = 10;
	public const int ExpectedJobCount = 11;

	public async ValueTask<BatchHandle> CreateAsync(
		Guid orderId,
		CancellationToken cancellationToken = default
	)
	{
		await using var batch = batches.Begin();

		var received = receiveOrder.Enqueue(
			new(orderId),
			batch
		);

		var inventory = reserveInventory.ScheduleAfter(
			new(orderId),
			received
		);
		var fraud = fraudCheck.ScheduleAfter(
			new(orderId),
			received
		);
		var payment = capturePayment.ScheduleAfter(
			new(orderId),
			received
		);

		var fulfillment = prepareFulfillment.ScheduleAfter(
			new(orderId),
			[inventory, fraud, payment]
		);

		var label = createShippingLabel.ScheduleAfter(
			new(orderId),
			fulfillment
		);
		var packed = packOrder.ScheduleAfter(
			new(orderId),
			fulfillment
		);

		var dispatched = dispatchOrder.ScheduleAfter(
			new(orderId),
			[label, packed]
		);
		var notified = notifyCustomer.ScheduleAfter(
			new(orderId),
			dispatched
		);

		writeAudit.ScheduleAfter(
			new(orderId),
			notified,
			ContinuationTrigger.Complete
		);

		return await batch.CommitAsync(cancellationToken);
	}
}
