import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function plan(env) {
  const enabled = key => env[`PUBLISH_${key}`] === 'true';
  if (!['WINDOWS', 'LINUX', 'ANDROID', 'WEB'].some(enabled)) {
    throw new Error('Select at least one publish target.');
  }
  const include = [];
  for (const [key, os, rid, head] of [
    ['WINDOWS', 'windows-latest', 'win-x64', 'Windows'],
    ['LINUX', 'ubuntu-24.04', 'linux-x64', 'Linux'],
  ]) {
    if (!enabled(key)) continue;
    for (const variant of ['self-contained', 'framework-dependent']) {
      include.push({ os, rid, variant,
        project: `src/Broiler.Writer.${head}/Broiler.Writer.${head}.csproj`,
        configuration: `Release-${head}`,
        executable: `Broiler.Writer.${head}${head === 'Windows' ? '.exe' : ''}`,
        assembly: `Broiler.Writer.${head}` });
    }
  }
  // GitHub evaluates matrices even for jobs that are skipped. Keep one valid
  // row when only mobile/web is selected; the build job condition skips it.
  return { include: include.length ? include : [{ os: 'ubuntu-24.04', rid: 'unused', variant: 'unused' }] };
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  appendFileSync(process.env.GITHUB_OUTPUT, `matrix=${JSON.stringify(plan(process.env))}\n`);
}
