// The Navigator on a fake desk: every level asked of the desk and laid on the slot keys, the
// modes, the text, the paging, BACK, FOLLOW — nothing known ahead of time but the rails.
import test from 'node:test'
import assert from 'node:assert/strict'
import { KNOWN_PROTOCOL, Navigator, NAV_SLOTS, payload, slotsOf, navVariableDefaults } from '../src/nav.js'

const entry = (id, text, extra = {}) => ({ id, text, detail: '', scope: 'live', tone: 'live', wire: '', menu: '', takesText: false, because: '', on: false, enabled: true, page: '', item: '', question: '', children: [], ...extra })
const menuOf = (kind, subject, title, groups) => ({ kind, subject, title, subtitle: '', tone: 'go', hue: '#C0CBDB', groups })

const navReply = {
	rails: [
		{ id: 'Show', label: 'SHOW', hue: '#2EE68A', pages: ['Panel', 'Run', 'Eye'] },
		{ id: 'Plan', label: 'PLAN', hue: '#6E9BFF', pages: ['Cues', 'Looks', 'Install'] },
	],
	pages: [
		{ header: 'Panel', rail: 'Show', hue: '#2EE68A', settings: false }, { header: 'Run', rail: 'Show', hue: '#2EE68A', settings: false }, { header: 'Eye', rail: 'Show', hue: '#F2D26B', settings: false },
		{ header: 'Cues', rail: 'Plan', hue: '#6E9BFF', settings: true }, { header: 'Looks', rail: 'Plan', hue: '#6E9BFF', settings: false }, { header: 'Install', rail: 'Plan', hue: '#9AB4FF', settings: false },
	],
	desk: { page: 'Panel', rail: 'SHOW', run: false },
}
const looksPage = menuOf('page', 'Looks', 'LOOKS', [
	{ heading: 'LOOKS', tone: 'live', note: '', entries: [
		entry('look:L1', 'Walk-in', { wire: 'LOOK Walk-in', menu: 'LOOK Walk-in', on: true }),
		entry('look:L2', 'Keynote', { wire: 'LOOK Keynote', menu: 'LOOK Keynote' }),
	] },
	{ heading: 'BUILD', tone: 'stack', note: '', entries: [
		entry('build.look.save', 'Save the preview as a look…', { scope: 'stack', tone: 'stack', wire: 'LOOK SAVE *', takesText: true }),
		entry('build.look.update', 'Update the look on air', { scope: 'stack', tone: 'stack', wire: 'LOOK UPDATE', enabled: false, because: 'No look is on air' }),
	] },
	{ heading: 'GO TO', tone: 'go', note: '', entries: [entry('go.page', 'Open on the desk', { scope: 'go', tone: 'go', wire: 'NAV Looks', page: 'Looks' })] },
])
const walkInMenu = menuOf('look', 'L1', 'Walk-in', [
	{ heading: 'IN THE PREVIEW', tone: 'preview', note: '', entries: [entry('look.preview', 'Into the preview', { scope: 'preview', tone: 'preview', wire: 'PVW LOOK Walk-in' })] },
	{ heading: 'TO AIR', tone: 'live', note: '', entries: [entry('look.air', 'On air now', { wire: 'LOOK Walk-in', on: true })] },
	{ heading: 'THE LOOK', tone: 'stack', note: '', entries: [entry('look.hotkey', 'F-key — F1', { scope: 'stack', tone: 'stack', children: [entry('look.hotkey:0', 'No F-key', { scope: 'stack', tone: 'stack' }), entry('look.hotkey:1', 'F1', { scope: 'stack', tone: 'stack', on: true }), entry('look.hotkey:2', 'F2', { scope: 'stack', tone: 'stack' })] })] },
])
const cuesPage = menuOf('page', 'Cues', 'CUES', [
	{ heading: 'THE STACK', tone: 'stack', note: '', entries: Array.from({ length: 30 }, (_, i) => entry(`cue:C${i + 1}`, `0${i + 1}.010 Cue ${i + 1}`, { scope: 'stack', tone: 'stack', wire: `CUE STANDBY 0${i + 1}.010`, menu: `CUE 0${i + 1}.010`, on: i === 0 })) },
	{ heading: 'GO TO', tone: 'go', note: '', entries: [entry('go.page', 'Open on the desk', { scope: 'go', tone: 'go', wire: 'NAV Cues', page: 'Cues' })] },
])

/** A desk on the other end of ask(): the questions answered from the tables above, every line remembered. */
function fakeDesk() {
	const asked = []
	const said = []
	const ask = async (line) => {
		asked.push(line)
		if (line === 'NAV') return 'OK ' + JSON.stringify(navReply)
		if (line === 'MENU PAGE Looks') return 'OK ' + JSON.stringify(looksPage)
		if (line === 'MENU PAGE Cues') return 'OK ' + JSON.stringify(cuesPage)
		if (line === 'MENU LOOK Walk-in') return 'OK ' + JSON.stringify(walkInMenu)
		if (line.startsWith('MENU ')) return `ERR no such thing '${line.slice(5)}'`
		if (line.startsWith('LOOK SAVE ')) return `OK Look '${line.slice(10)}' saved from the preview — 3 in the show.`
		return 'OK'
	}
	const say = (line) => said.push(line)
	return { ask, say, asked, said }
}

async function booted() {
	const desk = fakeDesk()
	const changes = []
	const nav = new Navigator({ ask: desk.ask, say: desk.say, changed: (n) => changes.push(n.level) })
	nav.learnDesk(payload(await desk.ask('NAV')))
	return { nav, desk, changes }
}

test('the reply payload is read only from an OK with JSON', () => {
	assert.deepEqual(payload('OK {"a":1}'), { a: 1 })
	assert.equal(payload('OK'), null)
	assert.equal(payload('ERR no'), null)
	assert.equal(payload('OK not json'), null)
	assert.equal(payload('OK 3'), null)
})

test('the rails are the first level, from the desk\'s own table once NAV has answered; a rail opens its pages; a page asks the desk for its menu', async () => {
	const { nav, desk } = await booted()
	assert.equal(nav.level, 'rails')
	assert.deepEqual(nav.slots.map((s) => s.text), ['SHOW', 'PLAN'])
	assert.equal(nav.slots[0].on, true) // the desk is on the Panel, a SHOW page
	assert.equal(nav.where, 'RAILS')
	await nav.press(2)
	assert.equal(nav.level, 'pages')
	assert.deepEqual(nav.slots.map((s) => s.text), ['Cues', 'Looks', 'Install'])
	assert.equal(nav.slots[0].wire, 'NAV Cues')
	assert.equal(nav.where, 'PLAN')
	await nav.press(2)
	assert.equal(nav.level, 'page')
	assert.equal(nav.page, 'Looks')
	assert.equal(nav.title, 'LOOKS')
	assert.equal(nav.where, 'PLAN › Looks')
	assert.deepEqual(desk.asked.slice(1), ['MENU PAGE Looks'])
	assert.deepEqual(nav.slots.map((s) => s.text), ['Walk-in', 'Keynote', 'Save the preview as a look…', 'Update the look on air', 'Open on the desk'])
	assert.equal(nav.slots[0].on, true)
	assert.equal(nav.slots[0].tone, 'live')
	assert.equal(nav.slots[2].takesText, true)
	assert.equal(nav.slots[3].enabled, false)
	assert.deepEqual(desk.said, []) // FOLLOW is off: the desk was not turned
	const v = nav.variables()
	assert.equal(v.nav_slot_1, 'Walk-in')
	assert.equal(v.nav_slot_3, 'Save the preview as a look… …')
	assert.equal(v.nav_slot_4_detail, 'No look is on air')
	assert.equal(v.nav_slot_1_group, 'LOOKS')
	assert.equal(v.nav_slot_1_wire, 'LOOK Walk-in')
	assert.equal(v.nav_level, 'page')
	assert.equal(v.nav_rail, 'PLAN')
	assert.equal(v.nav_pages, 'Cues · Looks · Install')
	assert.equal(v.nav_mode, 'MENU')
	assert.equal(v[`nav_slot_${NAV_SLOTS}`], '')
})

test('MENU mode opens a thing\'s own menu, a drawer its choices, BACK walks out again with the levels asked afresh; RUN mode fires the line and asks the level again', async () => {
	const { nav, desk } = await booted()
	nav.openRail('PLAN')
	await nav.openPage('Looks')
	assert.equal(await nav.press(1), 'menu LOOK Walk-in')
	assert.equal(nav.level, 'menu')
	assert.equal(nav.title, 'Walk-in')
	assert.equal(nav.where, 'PLAN › Looks › Walk-in')
	assert.deepEqual(nav.slots.map((s) => s.text), ['Into the preview', 'On air now', 'F-key — F1'])
	assert.equal(nav.slots[2].drawer, true)
	assert.equal(nav.variables().nav_slot_3, 'F-key — F1 ▸')
	assert.equal(await nav.press(3), 'drawer F-key — F1')
	assert.equal(nav.level, 'drawer')
	assert.deepEqual(nav.slots.map((s) => s.text), ['No F-key', 'F1', 'F2'])
	assert.equal(nav.slots[1].on, true)
	assert.equal(nav.where, 'PLAN › Looks › Walk-in › F-key — F1')
	await nav.back()
	assert.equal(nav.level, 'menu')
	assert.equal(nav.menuWords, 'LOOK Walk-in')
	// In the menu, a line fires (a menu entry has no menu of its own) and the menu is asked again.
	const before = desk.asked.length
	assert.equal(await nav.press(1), 'PVW LOOK Walk-in')
	assert.deepEqual(desk.asked.slice(before), ['PVW LOOK Walk-in', 'MENU LOOK Walk-in'])
	assert.equal(nav.reply, 'OK')
	await nav.back()
	assert.equal(nav.level, 'page')
	assert.equal(nav.page, 'Looks')
	await nav.back()
	assert.equal(nav.level, 'pages')
	await nav.back()
	assert.equal(nav.level, 'rails')
	await nav.back() // nothing behind the rails: still the rails
	assert.equal(nav.level, 'rails')

	// RUN mode: a thing's key fires its line rather than opening its menu.
	nav.setMode('run')
	assert.equal(nav.mode, 'run')
	nav.openRail('PLAN')
	await nav.openPage('Looks')
	const at = desk.asked.length
	assert.equal(await nav.press(2), 'LOOK Keynote')
	assert.deepEqual(desk.asked.slice(at), ['LOOK Keynote', 'MENU PAGE Looks'])
	nav.setMode('toggle')
	assert.equal(nav.mode, 'menu')
})

test('a text-taking key needs the words first, then sends the line with them and shows the desk\'s answer; a disabled key says why; the desk-only entries say so', async () => {
	const { nav, desk } = await booted()
	nav.openRail('PLAN')
	await nav.openPage('Looks')
	const at = desk.asked.length
	assert.equal(await nav.press(3), 'Type the words first (NAV TEXT)')
	assert.equal(desk.asked.length, at)
	nav.setText('  Doors open ')
	assert.equal(nav.text, 'Doors open')
	assert.equal(await nav.press(3), 'LOOK SAVE Doors open')
	assert.equal(desk.asked[at], 'LOOK SAVE Doors open')
	assert.match(nav.reply, /Look 'Doors open' saved/)
	assert.equal(nav.variables().nav_reply, nav.reply)
	assert.equal(await nav.press(4), 'No look is on air')
	assert.equal(await nav.press(5), 'NAV Looks') // a GO TO entry carries its wire
	assert.equal(await nav.press(24), 'nothing on that key')
})

test('a level with more entries than keys pages with NEXT and PREV, and the range says where the keys are', async () => {
	const { nav } = await booted()
	nav.openRail('PLAN')
	await nav.openPage('Cues')
	assert.equal(nav.count, 31)
	assert.equal(nav.slots.length, NAV_SLOTS)
	assert.equal(nav.range, `1–${NAV_SLOTS} of 31`)
	assert.equal(nav.slots[0].on, true)
	assert.equal(nav.next(), true)
	assert.equal(nav.offset, NAV_SLOTS)
	assert.equal(nav.slots.length, 31 - NAV_SLOTS)
	assert.equal(nav.range, `${NAV_SLOTS + 1}–31 of 31`)
	assert.equal(nav.slots.at(-1).text, 'Open on the desk')
	assert.equal(nav.next(), false)
	assert.equal(nav.prev(), true)
	assert.equal(nav.offset, 0)
	assert.equal(nav.prev(), false)
	// A press on a later page keeps the page after the level is asked again.
	nav.next()
	nav.setMode('run')
	await nav.press(1)
	assert.equal(nav.offset, NAV_SLOTS)
})

test('FOLLOW both ways: the deck turns the desk\'s page, and the desk\'s page turns the deck; off, neither moves the other', async () => {
	const { nav, desk } = await booted()
	nav.openRail('PLAN')
	await nav.openPage('Looks')
	assert.deepEqual(desk.said, [])
	await nav.deskMoved('Cues', false)
	assert.equal(nav.page, 'Looks') // FOLLOW off: the deck stays where it was
	nav.setFollow('on')
	assert.equal(nav.follow, true)
	await nav.openPage('Looks') // the desk is on Cues: the deck turns it
	assert.deepEqual(desk.said, ['NAV Looks'])
	await nav.deskMoved('Looks', false) // the desk is now where the deck sent it: nothing more
	assert.deepEqual(desk.said, ['NAV Looks'])
	await nav.deskMoved('Cues', false)
	assert.equal(nav.page, 'Cues') // the desk moved by hand: the deck followed
	assert.deepEqual(desk.said, ['NAV Looks']) // and did not send the desk anywhere
	await nav.deskMoved('Panel', true) // the Run surface has no page menu: the deck asks, nothing comes, the keys stay
	assert.equal(nav.page, 'Cues')
	await nav.openPage('Cues') // already where the desk is (Run counts as elsewhere): the deck turns it back to Cues
	assert.deepEqual(desk.said, ['NAV Looks', 'NAV Cues'])
	nav.home()
	await nav.deskMoved('Looks', false)
	assert.equal(nav.level, 'rails') // on the rails, the deck stays put
	nav.setFollow('toggle')
	assert.equal(nav.follow, false)
})

test('a stranger is refused without moving; the defaults are the rest values every STATE writes first', async () => {
	const { nav } = await booted()
	assert.equal(nav.openRail('Nowhere'), false)
	assert.equal(await nav.openPage('Nowhere'), false)
	assert.equal(await nav.openMenu('NOWHERE 9'), false)
	assert.equal(nav.level, 'rails')
	const d = navVariableDefaults()
	assert.equal(d.nav_level, 'rails')
	assert.equal(d.nav_slot_1, 'SHOW') // a deck at rest shows the rails
	assert.equal(d.nav_slot_6, '')
	assert.equal(d.nav_mode, 'MENU')
	assert.equal(Object.keys(d).length, 14 + NAV_SLOTS * 4) // round 80: desk_protocol joined the Navigator's own
	assert.deepEqual(slotsOf(null), [])
})

test('round 80 — the desk\'s descriptor version is read from NAV and MENU replies, told once per change, and read as a variable', async () => {
	const { nav } = await booted()
	assert.equal(KNOWN_PROTOCOL, 1)
	assert.equal(nav.protocol, 0) // the fake desk's table carries no version: nothing claimed
	assert.equal(nav.variables().desk_protocol, '')
	const told = []
	nav.onProtocol = (n) => told.push(n)
	nav.learnDesk({ ...navReply, protocol: 2 })
	assert.equal(nav.protocol, 2)
	assert.equal(nav.variables().desk_protocol, '2')
	nav.learnDesk({ ...navReply, protocol: 2 }) // the same again: not told twice
	nav.learnDesk({ ...navReply, protocol: 'x' }) // not a number: ignored
	assert.deepEqual(told, [2])
	// A MENU reply carries it too.
	const versioned = new Navigator({ ask: async () => 'OK ' + JSON.stringify({ ...looksPage, protocol: 3 }), say: () => {} })
	const seen = []
	versioned.onProtocol = (n) => seen.push(n)
	const menu = await versioned.menu('MENU PAGE Looks')
	assert.equal(menu.title, 'LOOKS')
	assert.deepEqual(seen, [3])
	assert.equal(versioned.variables().desk_protocol, '3')
	const refused = new Navigator({ ask: async () => 'ERR no such thing', say: () => {} })
	assert.equal(await refused.menu('MENU LOOK Nobody'), null)
	assert.equal(refused.protocol, 0)
})
