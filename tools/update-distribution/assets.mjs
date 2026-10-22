// Direct upload helper: scoped upload tokens enter over stdin, never CLI arguments.
import { readFile, readdir, stat } from 'node:fs/promises';
import { resolve, relative, extname, join } from 'node:path';
import { createHash } from 'node:crypto';
import { createInterface } from 'node:readline';

const root = resolve(process.argv[3] ?? '../../artifacts/update-distribution');
const types = { '.json': 'application/json', '.html': 'text/html; charset=utf-8', '.bin': 'application/octet-stream' };
const entries = [];
async function scan(dir) {
  for (const item of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, item.name);
    if (item.isDirectory()) await scan(path);
    else if (item.isFile() && !item.name.startsWith('_')) {
      const size = (await stat(path)).size;
      if (size > 25 * 1024 * 1024) throw new Error('Static asset exceeds 25 MiB limit');
      const bytes = await readFile(path);
      const hash = createHash('sha256').update(bytes.toString('base64') + extname(path).slice(1)).digest('hex').slice(0, 32);
      entries.push({ path, key: '/' + relative(root, path).replaceAll('\\', '/'), hash, size });
    }
  }
}
await scan(root);
if (process.argv[2] === 'manifest') {
  process.stdout.write(JSON.stringify(Object.fromEntries(entries.map(e => [e.key, { hash: e.hash, size: e.size }]))));
} else if (process.argv[2] === 'upload') {
  process.stdout.write('READY\n');
  const lines = createInterface({ input: process.stdin });
  const [line] = await (async () => { for await (const line of lines) return [line]; })();
  lines.close();
  const session = JSON.parse(line);
  const byHash = new Map(entries.map(e => [e.hash, e]));
  let completion = session.jwt;
  for (const bucket of session.buckets) {
    const form = new FormData();
    for (const hash of bucket) {
      const entry = byHash.get(hash);
      if (!entry) throw new Error('Server requested unknown asset');
      const bytes = await readFile(entry.path);
      form.append(hash, new Blob([bytes.toString('base64')], { type: types[extname(entry.path)] ?? 'application/octet-stream' }), hash);
    }
    const response = await fetch(`https://api.cloudflare.com/client/v4/accounts/${session.accountId}/workers/assets/upload?base64=true`, {
      method: 'POST', headers: { Authorization: `Bearer ${session.jwt}` }, body: form,
      signal: AbortSignal.timeout(120000)
    });
    const result = await response.json();
    if (!response.ok || !result.success) throw new Error(`Upload failed: ${response.status} ${JSON.stringify(result.errors)}`);
    if (result.result?.jwt) completion = result.result.jwt;
  }
  process.stdout.write(JSON.stringify({ jwt: completion }));
} else throw new Error('Use manifest or upload');
