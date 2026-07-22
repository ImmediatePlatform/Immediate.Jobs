using Immediate.Jobs.Aspire.Api.Jobs;
using Immediate.Jobs.Shared;

namespace Immediate.Jobs.Aspire.Api.Workflows;

public sealed class OrderFulfillmentWorkflow(
	IJobBatchScheduler batches,
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

		var received = await receiveOrder.AddToBatch(
			batch,
			new(orderId),
			cancellationToken: cancellationToken
		);

		var inventory = await reserveInventory.ScheduleAfter(
			received,
			new(orderId),
			cancellationToken: cancellationToken
		);
		var fraud = await fraudCheck.ScheduleAfter(
			received,
			new(orderId),
			cancellationToken: cancellationToken
		);
		var payment = await capturePayment.ScheduleAfter(
			received,
			new(orderId),
			cancellationToken: cancellationToken
		);

		var fulfillment = await prepareFulfillment.ScheduleAfter(
			[inventory, fraud, payment],
			new(orderId),
			cancellationToken: cancellationToken
		);

		var label = await createShippingLabel.ScheduleAfter(
			fulfillment,
			new(orderId),
			cancellationToken: cancellationToken
		);
		var packed = await packOrder.ScheduleAfter(
			fulfillment,
			new(orderId),
			cancellationToken: cancellationToken
		);

		var dispatched = await dispatchOrder.ScheduleAfter(
			[label, packed],
			new(orderId),
			cancellationToken: cancellationToken
		);
		var notified = await notifyCustomer.ScheduleAfter(
			dispatched,
			new(orderId),
			cancellationToken: cancellationToken
		);

		_ = await writeAudit.ScheduleAfter(
			notified,
			new(orderId),
			ContinuationTrigger.AllComplete,
			cancellationToken: cancellationToken
		);

		return await batch.CommitAsync(cancellationToken);
	}
}
