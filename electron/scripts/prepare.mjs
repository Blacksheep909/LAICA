// Stages what the app ships: the interface (frontend/dist) and the engine for one platform.
// Usage: node scripts/prepare.mjs <rid>      e.g. linux-x64, linux-arm64, osx-x64, osx-arm64
import { execFileSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const stage = join(here, '..', 'stage');
const rid = process.argv[2];
if (!rid) {
  console.error('Usage: node scripts/prepare.mjs <rid>   (linux-x64, linux-arm64, osx-x64, osx-arm64)');
  process.exit(2);
}
const dist = join(root, 'frontend', 'dist');
if (!existsSync(join(dist, 'index.html'))) {
  console.error('Build the interface first: cd frontend && pnpm install && pnpm build');
  process.exit(2);
}
// The app's version is the repository's VERSION file.
const pkgPath = join(here, '..', 'package.json');
const pkg = JSON.parse(readFileSync(pkgPath, 'utf8'));
pkg.version = readFileSync(join(root, 'VERSION'), 'utf8').trim();
writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n');
rmSync(stage, { recursive: true, force: true });
mkdirSync(stage, { recursive: true });
cpSync(dist, join(stage, 'ui'), { recursive: true });
execFileSync(
  'dotnet',
  [
    'publish', join(root, 'server', 'LAICA.Server.csproj'),
    '-c', 'Release', '-r', rid, '--self-contained',
    '-p:PublishSingleFile=true', '-p:DebugType=None', '-p:GenerateDocumentationFile=false',
    '-o', join(stage, 'server'),
  ],
  { stdio: 'inherit' },
);
console.log('Staged', rid, 'in', stage);
