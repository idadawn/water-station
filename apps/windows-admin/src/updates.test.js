import { strict as assert } from 'node:assert';
import { EventEmitter } from 'node:events';
import { setImmediate } from 'node:timers/promises';
import test from 'node:test';
import { startDesktopUpdates } from './updates.js';

test('checks at startup, retries after offline, and waits for exit once a download is ready', async t => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const updater = new EventEmitter();
  let checks = 0;
  let finishDownload;
  updater.checkForUpdates = async () => {
    if (++checks === 1) throw new Error('offline');
    return { downloadPromise: new Promise(resolve => { finishDownload = resolve; }) };
  };
  const notices = [];
  const stop = startDesktopUpdates(updater, version => notices.push(version), 100);
  assert.equal(updater.autoInstallOnAppQuit, true);
  assert.equal(updater.autoDownload, true);
  await setImmediate();
  t.mock.timers.tick(100);
  await setImmediate();
  t.mock.timers.tick(100);
  assert.equal(checks, 2);
  updater.emit('update-downloaded', { version: '1.2.1' });
  finishDownload();
  await setImmediate();
  t.mock.timers.tick(100);
  assert.deepEqual(notices, ['1.2.1']);
  assert.equal(checks, 2);
  stop();
});
