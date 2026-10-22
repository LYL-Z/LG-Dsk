// Independent TLS, signature, per-chunk and final-file verification. Does not install.
import https from 'node:https';
import { createHash, createPublicKey, verify, constants } from 'node:crypto';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'https://updates.chronokalamos.com';
const [output, verificationAddress] = process.argv.slice(2);
if (!output) throw Error('Supply a new output directory. Optional second argument overrides DNS for diagnostics only.');
await mkdir(output, { recursive: true });
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const started = performance.now();
function get(path, maximum) {
  return new Promise((accept, reject) => {
    const options = { timeout: 30000 };
    if (verificationAddress) options.lookup = (_, options, callback) => options.all
      ? callback(null, [{ address: verificationAddress, family: 4 }]) : callback(null, verificationAddress, 4);
    const request = https.get(origin + path, options, response => {
      if (response.statusCode !== 200) { response.resume(); reject(Error(`Unexpected status ${response.statusCode}: ${path}`)); return; }
      const bytes = []; let count = 0;
      response.on('data', chunk => { count += chunk.length; if (count > maximum) response.destroy(Error('Response exceeds bound')); else bytes.push(chunk); });
      response.on('error', reject);
      response.on('end', () => accept(Buffer.concat(bytes)));
    });
    request.on('timeout', () => request.destroy(Error('Connection timeout')));
    request.on('error', reject);
  });
}
const envelope = JSON.parse(await get('/stable.json', 524288));
const payload = Buffer.from(envelope.payload, 'base64');
const key = createPublicKey(await readFile(resolve('src/GlassDesk.AppServices/Resources/update-publisher-public.pem')));
if (!verify('sha256', payload, { key, padding: constants.RSA_PKCS1_PSS_PADDING, saltLength: 32 }, Buffer.from(envelope.signature, 'base64'))) throw Error('Publisher signature mismatch');
const release = JSON.parse(payload);
if (release.schemaVersion !== 1 || release.channel !== 'stable' || Date.parse(release.expiresUtc) <= Date.now()
  || !/^LG-Dsk-\d+\.\d+\.\d+-win-x64-Setup\.exe$/.test(release.fileName)
  || !Array.isArray(release.chunks) || release.chunks.length > 64 || release.size > 536870912
  || !release.chunks.every(c => /^[a-f0-9]{64}$/.test(c.sha256) && c.size > 0 && c.size <= 16777216)) throw Error('Invalid metadata');
const parts = new Array(release.chunks.length); let index = 0;
await Promise.all(Array.from({ length: 3 }, async () => {
  while (index < parts.length) {
    const i = index++, chunk = release.chunks[i];
    const bytes = await get(`/chunks/${chunk.sha256}.bin`, chunk.size);
    if (bytes.length !== chunk.size || digest(bytes) !== chunk.sha256) throw Error(`Invalid chunk ${i}`);
    parts[i] = bytes;
  }
}));
const installer = Buffer.concat(parts);
if (installer.length !== release.size || digest(installer) !== release.sha256) throw Error('Final installer mismatch');
await writeFile(resolve(output, release.fileName), installer);
const report = { origin, version: release.version, size: installer.length, sha256: digest(installer),
  chunks: parts.length, publisherSignature: true, tlsValidation: true, parallelism: 3,
  dnsOverrideForDiagnostics: Boolean(verificationAddress), elapsedSeconds: +((performance.now() - started) / 1000).toFixed(2),
  verifiedAt: new Date().toISOString(), limitation: 'One machine and one network; not worldwide or mainland availability acceptance.' };
await writeFile(resolve(output, 'verification.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report));
