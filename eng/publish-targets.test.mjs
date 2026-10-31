import { test } from 'node:test';
import assert from 'node:assert/strict';
import { plan } from './publish-targets.mjs';
for (let mask = 0; mask < 16; mask++) {
  test(`target selection ${mask}`, () => {
    const keys = ['WINDOWS', 'LINUX', 'ANDROID', 'WEB'];
    const env = Object.fromEntries(keys.map((key, i) => [`PUBLISH_${key}`, String(Boolean(mask & (1 << i)))]));
    if (!mask) { assert.throws(() => plan(env), /at least one/); return; }
    const rows = plan(env).include.filter(row => row.rid !== 'unused');
    assert.equal(rows.length, ((mask & 1 ? 1 : 0) + (mask & 2 ? 1 : 0)) * 2);
    assert.equal(rows.some(row => row.rid === 'win-x64'), Boolean(mask & 1));
    assert.equal(rows.some(row => row.rid === 'linux-x64'), Boolean(mask & 2));
  });
}
