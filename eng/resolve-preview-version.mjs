// Chooses the next Broiler.Writer preview version for the Publish workflow.
//
// Adapted from Broiler.Documents' eng/resolve-preview-version.mjs. The difference is where
// the package list comes from: the Writer's projects are applications and none of them is
// packable, so the package ids are the per-platform ones eng/package produces, and the
// configured floor is BroilerWriterVersion from Directory.Build.props.
import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));

// One package per published platform. Keep in step with the matrix in publish.yml.
export const packageIds = [
  'Broiler.Writer.win-x64',
  'Broiler.Writer.linux-x64',
  'Broiler.Writer.android-arm64',
];

function parsePreview(version) {
  const match = /^(\d+\.\d+\.\d+)-preview\.([1-9]\d*)$/.exec(version);
  if (!match) throw new Error(`Only X.Y.Z-preview.N versions (N >= 1) may be published: '${version}'.`);
  return { prefix: match[1], number: BigInt(match[2]) };
}

export function chooseVersion(configured, published, { suffix = '' } = {}) {
  const { prefix, number: floor } = parsePreview(configured);
  let next = floor;
  for (const version of published) {
    const match = /^(\d+\.\d+\.\d+)-preview\.([1-9]\d*)$/i.exec(version);
    if (match && match[1] === prefix && BigInt(match[2]) >= next) {
      next = BigInt(match[2]) + 1n;
    }
  }

  const automatic = `${prefix}-preview.${next}`;
  const requested = suffix ? `${prefix}-${suffix}` : automatic;
  const preview = parsePreview(requested);
  if (preview.prefix !== prefix || preview.number < next) {
    throw new Error(`Requested '${requested}' must use ${prefix} and be at least '${automatic}'.`);
  }
  return requested;
}

async function readJson(url, headers, allowMissing, fetchImpl) {
  const response = await fetchImpl(url, { headers, signal: AbortSignal.timeout(30_000) });
  if (allowMissing && response.status === 404) return null;
  if (!response.ok) throw new Error(`Version lookup failed: HTTP ${response.status} from ${url}`);
  return response.json();
}

// PackageBaseAddress includes unlisted versions. Search results do not.
// https://learn.microsoft.com/nuget/api/package-base-address-resource
export async function readVersions(source, ids, headers = {}, fetchImpl = fetch) {
  const index = await readJson(source, headers, false, fetchImpl);
  const base = index.resources?.find(resource =>
    resource['@type'] === 'PackageBaseAddress/3.0.0')?.['@id'];
  if (!base) throw new Error(`No PackageBaseAddress resource in ${source}.`);
  const results = await Promise.all(ids.map(async packageId => {
    const url = `${base.replace(/\/$/, '')}/${packageId.toLowerCase()}/index.json`;
    const result = await readJson(url, headers, true, fetchImpl);
    if (result === null) return [];
    if (!Array.isArray(result.versions) || result.versions.some(value => typeof value !== 'string')) {
      throw new Error(`Invalid package version response for ${packageId}.`);
    }
    return result.versions;
  }));
  return results.flat();
}

function readConfiguredVersion() {
  const output = execFileSync('dotnet', [
    'msbuild', 'eng/package/Broiler.Writer.Package.csproj', '-nologo',
    '-getProperty:BroilerWriterVersion',
  ], { cwd: root, encoding: 'utf8' });
  return output.trim();
}

async function main() {
  const configured = readConfiguredVersion();
  parsePreview(configured);
  const target = process.env.TARGET || 'broiler-github';
  if (!['nuget.org', 'broiler-github'].includes(target)) throw new Error(`Unknown target '${target}'.`);
  // Preview numbers are cumulative across both feeds, whichever one is the
  // target: with preview.3 on GitHub Packages and preview.2 on nuget.org, the
  // next publish to either feed is preview.4, so a number never names two builds.
  const { GITHUB_REPOSITORY_OWNER: owner, GITHUB_ACTOR: actor, GITHUB_TOKEN: token } = process.env;
  if (!owner || !actor || !token) throw new Error('GitHub feed lookup requires owner, actor, and token.');
  const authorization = `Basic ${Buffer.from(`${actor}:${token}`).toString('base64')}`;
  const published = (await Promise.all([
    readVersions('https://api.nuget.org/v3/index.json', packageIds),
    readVersions(`https://nuget.pkg.github.com/${owner}/index.json`, packageIds, { authorization }),
  ])).flat();
  const version = chooseVersion(configured, published, { suffix: process.env.VERSION_SUFFIX || '' });
  // Android's versionCode must be a positive integer that only ever increases; the
  // preview number is exactly that, since it is cumulative across both feeds.
  const versionCode = parsePreview(version).number.toString();
  console.log(`Version: ${version} (versionCode ${versionCode}) -> ${target} (dry-run: ${process.env.DRY_RUN ?? 'true'})`);
  if (process.env.GITHUB_OUTPUT) {
    appendFileSync(process.env.GITHUB_OUTPUT, `version=${version}\nversion-code=${versionCode}\n`);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
