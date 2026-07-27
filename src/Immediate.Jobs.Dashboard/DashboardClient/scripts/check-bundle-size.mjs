import { readFile } from 'node:fs/promises';
import { gzipSync } from 'node:zlib';

const maximumBytes = 200 * 1024;
const assets = await Promise.all([
	readFile(new URL('../../Assets/app.js', import.meta.url)),
	readFile(new URL('../../Assets/app.css', import.meta.url)),
]);
const compressedBytes = assets.reduce(
	(total, asset) => total + gzipSync(asset).byteLength,
	0,
);

if (compressedBytes > maximumBytes) {
	throw new Error(`Dashboard bundle is ${compressedBytes} bytes gzipped; limit is ${maximumBytes}.`);
}

console.log(`Dashboard bundle: ${compressedBytes} bytes gzipped.`);
