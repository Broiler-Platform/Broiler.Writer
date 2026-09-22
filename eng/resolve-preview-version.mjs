// Chooses the next Broiler.Writer preview version for the Publish workflow.
//
// The workflow does not push its packages anywhere, so the record of what has been built is
// the repository's tags: every successful run tags its commit writer-v<version>. The next
// version is the configured floor, BroilerWriterVersion from Directory.Build.props, raised
// past every tagged preview on the same release line.
import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));

export const tagPrefix = 'writer-v';

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

// Tags that are not ours (other prefixes, other products) are ignored rather than rejected.
export function versionsFromTags(tags) {
  return tags
    .map(tag => tag.trim())
    .filter(tag => tag.startsWith(tagPrefix))
    .map(tag => tag.slice(tagPrefix.length));
}

function git(...args) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8' });
}

function readConfiguredVersion() {
  const output = execFileSync('dotnet', [
    'msbuild', 'src/Broiler.Writer/Broiler.Writer.Core.csproj', '-nologo',
    '-getProperty:BroilerWriterVersion',
  ], { cwd: root, encoding: 'utf8' });
  return output.trim();
}

function main() {
  const configured = readConfiguredVersion();
  parsePreview(configured);
  // A shallow checkout carries no tags; ask the remote rather than trusting the clone.
  const tags = git('ls-remote', '--tags', '--refs', 'origin', `${tagPrefix}*`)
    .split('\n')
    .filter(Boolean)
    .map(line => line.split('\t')[1].replace(/^refs\/tags\//, ''));
  const version = chooseVersion(configured, versionsFromTags(tags), {
    suffix: process.env.VERSION_SUFFIX || '',
  });
  // Android's versionCode has to grow with every release (an update installs only over a
  // lower or equal one, and Play accepts an upload only above the last). The preview number
  // does exactly that.
  const versionCode = parsePreview(version).number.toString();
  console.log(`Version: ${version} (versionCode ${versionCode}, ${tags.length} earlier tags)`);
  if (process.env.GITHUB_OUTPUT) {
    appendFileSync(process.env.GITHUB_OUTPUT,
      `version=${version}\nversion-code=${versionCode}\ntag=${tagPrefix}${version}\n`);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main();
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
