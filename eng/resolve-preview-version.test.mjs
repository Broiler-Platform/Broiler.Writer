import assert from 'node:assert/strict';
import test from 'node:test';
import { chooseVersion, versionsFromTags } from './resolve-preview-version.mjs';

test('first publish uses the configured preview; later publishes increment numerically', () => {
  assert.equal(chooseVersion('0.1.0-preview.1', []), '0.1.0-preview.1');
  assert.equal(chooseVersion('0.1.0-preview.1', ['0.1.0-preview.1']), '0.1.0-preview.2');
  assert.equal(chooseVersion('0.1.0-preview.1', ['0.1.0-preview.9', '0.1.0-preview.10']), '0.1.0-preview.11');
});

test('configured preview is a floor and other release lines do not affect it', () => {
  assert.equal(chooseVersion('0.1.0-preview.4', [
    '0.1.0-preview.1', '0.2.0-preview.99', '0.1.0', '0.1.0-rc.9',
  ]), '0.1.0-preview.4');
});

test('only unused previews on the configured release line are accepted', () => {
  const published = ['0.1.0-preview.1'];
  assert.equal(chooseVersion('0.1.0-preview.1', published, { suffix: 'preview.3' }), '0.1.0-preview.3');
  assert.equal(chooseVersion('0.1.0-preview.1', published, { suffix: 'preview.2' }), '0.1.0-preview.2');
  for (const suffix of ['preview.1', 'rc.2', 'preview.0', 'preview.02', 'preview.2;evil']) {
    assert.throws(() => chooseVersion('0.1.0-preview.1', published, { suffix }));
  }
  assert.throws(() => chooseVersion('0.1.0', published));
});

test('only writer-v tags count as earlier versions', () => {
  const tags = ['writer-v0.1.0-preview.4', 'v0.1.0-preview.9', 'plate-v0.1.0-preview.7', ' writer-v0.1.0-preview.5\n'];
  assert.deepEqual(versionsFromTags(tags), ['0.1.0-preview.4', '0.1.0-preview.5']);
  assert.equal(chooseVersion('0.1.0-preview.1', versionsFromTags(tags)), '0.1.0-preview.6');
});
