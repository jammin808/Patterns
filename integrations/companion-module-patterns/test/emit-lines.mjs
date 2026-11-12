// Every line the module can put on the wire, one per line on stdout: each action pressed with every
// choice of its dropdowns and a sample for its other fields. The desk's own tests parse the file
// this writes (test/lines.txt), so a verb the module spells wrongly fails on the desk's side too.
import { boot, pressAction } from './harness.mjs'

const SAMPLES = {
	name: 'Walk-in', look: 'Walk-in', cue: '01.020', letter: 'A', kind: 'Grid', page: '', address: 'https://example.com/deck', key: 'ArrowRight',
	text: 'Hello there', what: 'Closing time', device: 'Projector', target: 'SCREEN 2', person: '2', design: '1', minutes: '5', time: '19:30', label: 'SHOW STARTS IN',
	delta: '+2:00', game: 'pong', line: 'PING', word: '', show: 'results',
	destination: 'Info HDMI', screen: 'INFO', time: '1:23', output: 'Info HDMI',
	host: '10.0.0.9:9924', node: 'a1b2c3d4', window: 'lobby',
}

// A text field pressed both ways: empty and with a sample — a clip verb's key, whose empty form is the clip on air's line.
const BOTH = { clip: ['', 'vid:clips/b roll.mp4'] }

function sample(field) {
	if (field.type === 'number') return field.default ?? field.min ?? 1
	if (field.type === 'checkbox') return field.default ?? false
	if (field.type === 'textinput') return SAMPLES[field.id] ?? field.default ?? 'x'
	return field.default
}

export async function allLines() {
	const b = await boot()
	const out = new Set()
	for (const [id, def] of Object.entries(b.ctx.actions)) {
		const fields = def.options ?? []
		const drops = fields.filter((f) => f.type === 'dropdown')
		let combos = [{}]
		for (const d of drops) combos = combos.flatMap((c) => d.choices.map((ch) => ({ ...c, [d.id]: ch.id })))
		for (const f of fields.filter((x) => x.type === 'textinput' && BOTH[x.id])) combos = combos.flatMap((c) => BOTH[f.id].map((v) => ({ ...c, [f.id]: v })))
		for (const combo of combos) {
			const options = {}
			for (const f of fields) options[f.id] = f.id in combo ? combo[f.id] : sample(f)
			for (const line of pressAction(b, id, options)) out.add(line)
		}
	}
	return [...out].sort()
}

if (import.meta.url === `file://${process.argv[1]}`) {
	const lines = await allLines()
	process.stdout.write(lines.join('\n') + '\n')
}
