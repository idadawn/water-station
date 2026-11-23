/** Keep serving the open window while NSIS downloads and verifies an update. Install on normal exit. */
export function startDesktopUpdates(updater, notifyReady, intervalMs = 4 * 60 * 60 * 1000) {
  updater.autoDownload = true;
  updater.autoInstallOnAppQuit = true;
  updater.disableWebInstaller = true;
  let checking = false;
  let ready = false;
  const onDownloaded = ({ version }) => {
    if (ready) return;
    ready = true;
    notifyReady(version);
  };
  const onError = error => console.warn('客户端更新失败', error.message);
  updater.on('update-downloaded', onDownloaded);
  updater.on('error', onError);
  const check = async () => {
    if (checking || ready) return;
    checking = true;
    try {
      const result = await updater.checkForUpdates();
      await result?.downloadPromise;
    } catch {
      // electron-updater emits the error above. An offline client retries at the next interval.
    } finally {
      checking = false;
    }
  };
  void check();
  const timer = setInterval(check, intervalMs);
  timer.unref();
  return () => {
    clearInterval(timer);
    updater.off('update-downloaded', onDownloaded);
    updater.off('error', onError);
  };
}
