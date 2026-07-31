import { afterEach, vi } from 'vitest';

afterEach(() => {
	vi.restoreAllMocks();
	vi.unstubAllGlobals();
});

Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
	configurable: true,
	value() {
		this.setAttribute('open', '');
	},
});

Object.defineProperty(HTMLDialogElement.prototype, 'close', {
	configurable: true,
	value() {
		this.removeAttribute('open');
		this.dispatchEvent(new Event('close'));
	},
});

Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', {
	configurable: true,
	value() {},
});

Object.defineProperty(HTMLElement.prototype, 'scrollTo', {
	configurable: true,
	value() {},
});
