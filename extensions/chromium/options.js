const $ = (id) => document.getElementById(id);

// Firefox's promise-based `browser` where it exists, Chrome's `chrome` otherwise.
const api = globalThis.browser ?? globalThis.chrome;

function show(state, detail, kind) {
  $('state').textContent = state;
  $('detail').textContent = detail;
  $('dot').className = `dot ${kind}`;
}

function say(message, kind = '') {
  $('status').textContent = message;
  $('status').className = kind;
}

/** Asks the background worker, which owns discovery and pairing. */
async function connect() {
  $('connect').disabled = true;
  show('Connecting…', 'Looking for Stretto on this computer.', 'warn');

  try {
    const result = await api.runtime.sendMessage({ type: 'stretto-connect' });

    if (result?.ok) {
      show('Connected', `Stretto is listening on port ${result.port}. Nothing to set up.`, 'ok');
    } else {
      // Two very different causes, and the user can act on both.
      show('Not connected',
           'Start Stretto, then press Connect. If it is already running, check that you '
           + 'allowed this extension when it asked.',
           'bad');
    }
  } catch {
    show('Not connected', 'The extension could not reach its background worker.', 'bad');
  } finally {
    $('connect').disabled = false;
    await showSites();
  }
}

/** The app's skip list, as last fetched. Removing one goes through the app, which owns it. */
async function showSites() {
  const { rules } = await api.storage.local.get({ rules: { skipHosts: [] } });
  const sites = [...(rules.skipHosts || [])].filter(Boolean).sort();

  const list = $('sites');
  list.textContent = '';
  $('no-sites').hidden = sites.length > 0;

  for (const host of sites) {
    const item = document.createElement('li');
    const name = document.createElement('span');
    name.textContent = host;
    name.title = host;

    const remove = document.createElement('button');
    remove.textContent = 'Capture again';
    remove.addEventListener('click', async () => {
      remove.disabled = true;
      try {
        const result = await api.runtime.sendMessage({ type: 'stretto-skip-host', host, skip: false });
        if (!result?.ok) throw new Error();
        say(`Downloads from ${host} go to Stretto again.`, 'ok');
        await showSites();
      } catch {
        remove.disabled = false;
        say('Could not change that. Is Stretto running?', 'bad');
      }
    });

    item.append(name, remove);
    list.append(item);
  }
}

async function load() {
  const cfg = await api.storage.local.get({ enabled: true, notifications: true });
  $('enabled').checked = cfg.enabled !== false;
  $('notifications').checked = cfg.notifications !== false;
  $('version').textContent = `Stretto extension ${api.runtime.getManifest().version}`;

  await showSites();

  // Checking on open means the page reflects reality rather than the last saved guess.
  await connect();
}

$('enabled').addEventListener('change', () =>
  api.storage.local.set({ enabled: $('enabled').checked }));

$('notifications').addEventListener('change', () =>
  api.storage.local.set({ notifications: $('notifications').checked }));

$('forget').addEventListener('click', async () => {
  await api.storage.local.set({ token: '', port: 0 });
  say('Pairing forgotten. Press Connect to pair again.');
  show('Not paired', 'Press Connect; Stretto will ask you to allow this extension.', 'warn');
});

$('connect').addEventListener('click', connect);

load();
