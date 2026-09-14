// The package Companion imports, checked the way Companion 5.0.5 checks it. The desk never sees
// Companion here, so this test stands in for the Modules page: it builds the .tgz with the
// module tools, opens it as Companion's installer does (gunzip, one root folder stripped,
// companion/manifest.json found), validates the manifest with the module base's own validator
// (strict, not loose), holds the runtime to one Companion 5 bundles, the API version to the
// host's, the entrypoint to a file in the package that loads under Node, and the version equal
// wherever it is read — the manifest, the package, the desk's HELLO.
import test from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { readFileSync, existsSync, mkdtempSync, writeFileSync, mkdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, dirname, resolve, posix } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { gunzipSync } from 'node:zlib'
import { validateManifest } from '@companion-module/base/manifest'
import { MODULE_VERSION } from '../src/main.js'

const here = dirname(fileURLToPath(import.meta.url))
const root = resolve(here, '..')
const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'))
const sourceManifest = JSON.parse(readFileSync(join(root, 'companion/manifest.json'), 'utf8'))
const baseVersion = JSON.parse(readFileSync(join(root, 'node_modules/@companion-module/base/package.json'), 'utf8')).version

/** What Companion 5.0.5 ships a runtime for (assets/nodejs-versions.json). */
const RUNTIMES_COMPANION_5_BUNDLES = ['node18', 'node22', 'node26']

/** The tar as a list of {name, type, data} — ustar with GNU long names and pax headers honoured, which is all the module tools write. */
function readTar(buf) {
	const entries = []
	let at = 0
	let longName = null
	let paxPath = null
	while (at + 512 <= buf.length) {
		const header = buf.subarray(at, at + 512)
		if (header.every((b) => b === 0)) break
		const field = (start, len) => header.subarray(start, start + len).toString('utf8').replace(/\0.*$/s, '')
		let name = field(0, 100)
		const size = parseInt(field(124, 12).trim() || '0', 8)
		const type = String.fromCharCode(header[156]) || '0'
		const prefix = field(345, 155)
		if (prefix) name = prefix + '/' + name
		const data = buf.subarray(at + 512, at + 512 + size)
		at += 512 + Math.ceil(size / 512) * 512
		if (type === 'L') { longName = data.toString('utf8').replace(/\0.*$/s, ''); continue }
		if (type === 'x') { const m = /(\d+) path=([^\n]*)\n/.exec(data.toString('utf8')); if (m) paxPath = m[2]; continue }
		if (type === 'g') continue
		if (paxPath) { name = paxPath; paxPath = null }
		if (longName) { name = longName; longName = null }
		entries.push({ name, type, data })
	}
	return entries
}

const tgz = join(root, `${sourceManifest.id}-${pkg.version}.tgz`)

test('the module tools build the package Companion imports', () => {
	execFileSync(process.execPath, [join(root, 'node_modules/@companion-module/tools/dist/scripts/build-connection.js')], { cwd: root, stdio: 'pipe' })
	assert.ok(existsSync(tgz), `the build wrote ${tgz}`)
})

test('the package opens the way Companion 5 opens it: gzip, one root folder, the manifest inside', () => {
	const tar = gunzipSync(readFileSync(tgz))                                          // "Failed to decompress data" otherwise
	const entries = readTar(tar)
	assert.ok(entries.length > 0)
	const roots = new Set(entries.map((e) => e.name.split('/')[0]))
	assert.equal(roots.size, 1, `one root folder, stripped on import: ${[...roots]}`)      // tarfs.extract(..., { strip: 1 })
	const rootDir = [...roots][0]
	const manifestEntry = entries.find((e) => e.name === `${rootDir}/companion/manifest.json`)
	assert.ok(manifestEntry, "Doesn't look like a valid module, missing manifest")      // Companion's own words
	assert.ok(!entries.some((e) => e.name.includes('/node_modules/')), 'the bundle carries its dependencies; nothing is loaded from node_modules')
	const files = new Map(entries.map((e) => [e.name.slice(rootDir.length + 1), e]))

	// The manifest, under the module base's schema — strict, so a template word would fail here before it fails in a review.
	const manifest = JSON.parse(manifestEntry.data.toString('utf8'))
	validateManifest(manifest, false)
	assert.equal(manifest.type, 'connection')
	assert.equal(manifest.id, sourceManifest.id)
	assert.match(manifest.version, /^\d+\.\d+\.\d+$/, 'Invalid module version')          // semver.parse(version) on import

	// The runtime and the API the host carries.
	assert.ok(RUNTIMES_COMPANION_5_BUNDLES.includes(manifest.runtime.type), `runtime ${manifest.runtime.type}: Companion 5 bundles ${RUNTIMES_COMPANION_5_BUNDLES}`)
	assert.equal(manifest.runtime.api, 'nodejs-ipc')
	const [major, minor] = manifest.runtime.apiVersion.split('.')
	assert.equal(`${major}.${minor}`, baseVersion.split('.').slice(0, 2).join('.'), `apiVersion ${manifest.runtime.apiVersion} is the module base's line (${baseVersion})`)

	// The entrypoint: a file in the package, relative to the manifest, that loads under Node.
	const entry = posix.normalize(posix.join('companion', manifest.runtime.entrypoint))
	assert.ok(files.has(entry), `entrypoint ${manifest.runtime.entrypoint} → ${entry} is in the package`)
	assert.ok(files.get(entry).data.length > 10_000, 'the entrypoint is the bundle, not a stub')
	const packageJson = JSON.parse(files.get('package.json').data.toString('utf8'))
	assert.equal(packageJson.type, 'module', 'the bundle is ES modules; package.json must say so')
	assert.equal(packageJson.version, manifest.version)
	assert.ok(files.has('LICENSE') && files.get('LICENSE').data.length > 100, 'the licence travels with the package, not as an empty file')
	assert.ok(files.has('companion/HELP.md'), 'the help travels with the package')
})

test('the entrypoint loads under this Node with no Companion on the other end', async () => {
	const tar = gunzipSync(readFileSync(tgz))
	const entries = readTar(tar)
	const rootDir = entries[0].name.split('/')[0]
	const dir = mkdtempSync(join(tmpdir(), 'patterns-module-'))
	for (const e of entries) {
		if (e.type !== '0' && e.type !== '') continue
		const target = join(dir, e.name.slice(rootDir.length + 1))
		mkdirSync(dirname(target), { recursive: true })
		writeFileSync(target, e.data)
	}
	// runEntrypoint() waits for the host's IPC handshake, which never comes here; the import itself proves the bundle parses and its top level runs.
	const loaded = await import(pathToFileURL(join(dir, 'main.js')).href)
	assert.ok(loaded, 'the bundle imported')
})

test('the version is one number everywhere it is read', () => {
	assert.equal(sourceManifest.version, pkg.version)
	assert.equal(MODULE_VERSION, pkg.version)
	const desk = readFileSync(join(root, '../../src/Patterns.Core/Services/CompanionWords.cs'), 'utf8')
	assert.ok(desk.includes(`public const string Version = "${pkg.version}";`), 'the desk says the same version is current')
	const readme = readFileSync(join(root, 'README.md'), 'utf8')
	assert.ok(readme.includes(`version **${pkg.version}**`), 'the README names the version')
	assert.ok(readme.includes(`module=${pkg.version}`), 'the README shows the HELLO with the version')
})
