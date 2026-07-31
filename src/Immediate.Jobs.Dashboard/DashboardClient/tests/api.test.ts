import { describe, expect, it, vi } from 'vitest';

import { ApiError, cancelJob, getJobs, request } from '@/api';

describe('dashboard API client', () => {
	it('requests server-side job pages of fifty with encoded filters', async () => {
		const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({
			items: [], skip: 50, take: 50, hasNext: false,
		}), { headers: { 'content-type': 'application/json' } }));

		await getJobs({ search: 'send email', queue: 'priority/jobs', state: 'Failed', page: 2 });
		const requestedUrl = new URL(String(fetchMock.mock.calls[0]?.[0]));
		expect(requestedUrl.searchParams.get('skip')).toBe('50');
		expect(requestedUrl.searchParams.get('take')).toBe('50');
		expect(requestedUrl.searchParams.get('search')).toBe('send email');
		expect(requestedUrl.searchParams.get('queue')).toBe('priority/jobs');
		expect(requestedUrl.searchParams.get('state')).toBe('Failed');
	});

	it('cancels jobs through an encoded dashboard route', async () => {
		const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 204 }));

		await cancelJob('job/one');

		const requestedUrl = new URL(String(fetchMock.mock.calls[0]?.[0]));
		expect(requestedUrl.pathname.endsWith('/api/jobs/job%2Fone/cancel')).toBe(true);
		expect(fetchMock.mock.calls[0]?.[1]).toEqual(expect.objectContaining({ method: 'POST' }));
	});

	it('handles empty successful responses and structured problems', async () => {
		vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(new Response(null, { status: 204 }));
		await expect(request<void>('jobs/job-1/retry', { method: 'POST' })).resolves.toBeUndefined();

		vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(new Response(JSON.stringify({
			title: 'Conflict', detail: 'The job is not failed.', status: 409,
		}), { status: 409, headers: { 'content-type': 'application/problem+json' } }));
		await expect(request('jobs/job-1/retry', { method: 'POST' })).rejects.toEqual(
			expect.objectContaining<ApiError>({ status: 409, message: 'The job is not failed.' }),
		);
	});
});
