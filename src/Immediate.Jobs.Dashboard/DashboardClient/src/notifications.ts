import { ref } from 'vue';

export interface Notification {
	id: number;
	message: string;
	tone: 'success' | 'error';
}

export const notifications = ref<Notification[]>([]);

let nextNotificationId = 1;

export function notify(message: string, tone: Notification['tone'] = 'success'): void {
	const id = nextNotificationId++;
	notifications.value = [...notifications.value, { id, message, tone }];
	window.setTimeout(() => dismissNotification(id), 4_500);
}

export function dismissNotification(id: number): void {
	notifications.value = notifications.value.filter((notification) => notification.id !== id);
}

export function errorText(reason: unknown): string {
	return reason instanceof Error ? reason.message : 'The request could not be completed.';
}
