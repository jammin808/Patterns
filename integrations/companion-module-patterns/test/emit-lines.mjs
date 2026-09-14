// Every line the module can put on the wire, one per line on stdout: each action pressed with every
// choice of its dropdowns and a sample for its other fields. The desk's own tests parse the file
// this writes (test/lines.txt), so a verb the module spells wrongly fails on the desk's side too.
import { boot, pressAction } from './harness.mjs'

const SAMPLES = {
	name: 'Walk-in', look: 'Walk-in', cue: '01.020', letter: 'A', kind: 'Grid', page: '', address: 'https://example.com/deck', key: 'ArrowRight',
	text: 'Hello there', what: 'Closing time', device: 'Projector', target: 'SCREEN 2', person: '2', design: '1', minutes: '5', time: '19:30', label: 'SHOW STARTS IN',
	delta: '+2:00', game: 'pong', line: 'PING', word: '', show: 'results',
	destination: 'Info HDMI', screen: 'INFO', time: '1:23',
}

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
		for (const combo of combos) {
			const options = {}
			for (const f of fields) options[f.id] = f.type === 'dropdown' ? combo[f.id] : sample(f)
			for (const line of pressAction(b, id, options)) out.add(line)
		}
	}
	return [...out].sort()
}

if (import.meta.url === `file://${process.argv[1]}`) {
	const lines = await allLines()
	process.stdout.write(lines.join('\n') + '\n')
}
