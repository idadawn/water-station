// Check exactly the files a store PC will download, for the environment just built.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync, statSync } from 'node:fs';
import { resolve } from 'node:path';
import { DEFAULT_ORIGIN, parseOrigin } from '../src/rules.js';

const { version } = JSON.parse(readFileSync(new URL('../package.json', import.meta.url)));
const origin = parseOrigin(process.env.KUNLUN_DESKTOP_ORIGIN || DEFAULT_ORIGIN);
const test = origin !== DEFAULT_ORIGIN;
const dir = resolve(test ? 'release/test' : 'release');
const file = `kunlun-admin${test ? '-test' : ''}-setup-${version}.exe`;
const manifest = readFileSync(resolve(dir, 'latest.yml'), 'utf8');
const config = readFileSync(resolve(dir, 'win-unpacked/resources/app-update.yml'), 'utf8');
const checksum = createHash('sha512').update(readFileSync(resolve(dir, file))).digest('base64');
for (const field of [`version: ${version}`, `url: ${file}`, `sha512: ${checksum}`, `size: ${statSync(resolve(dir, file)).size}`]) {
  assert.ok(manifest.includes(field), `Missing from latest.yml: ${field}`);
}
assert.ok(statSync(resolve(dir, file + '.blockmap')).size > 0);
assert.ok(config.includes(`url: ${origin}/downloads/windows/`));
console.log(`Verified ${file} and its update feed for ${origin}`);
