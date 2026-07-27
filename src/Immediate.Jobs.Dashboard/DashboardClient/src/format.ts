export function formatDate(value: string | null | undefined): string {
	return value ? new Date(value).toLocaleString() : '—';
}

export function formatJson(value: string): string {
	try {
		return JSON.stringify(JSON.parse(value), null, 2);
	} catch {
		return value;
	}
}
