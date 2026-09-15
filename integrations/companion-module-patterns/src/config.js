// The connection's settings: the desk found on the network (Companion's bonjour list of every
// Patterns advertising _patterns._tcp) or typed by hand, the wire's port, and the preset groups a
// desk ticks so its key list holds only what it uses.
import { Regex } from '@companion-module/base'

export const DEFAULT_PORT = 9697

/** The groups a user ticks; every preset section belongs to one. */
export const GROUPS = [
	{ id: 'transport', label: 'Transport — outputs, blackout, freeze, fade, review', def: true },
	{ id: 'cues', label: 'Cue stack — GO, standby, hold, arm, stop all, the cue bank', def: true },
	{ id: 'looks', label: 'All looks — F-keys, the look bank, every look of this show', def: true },
	{ id: 'patterns', label: 'All patterns — a key per kind of picture', def: false },
	{ id: 'clock', label: 'Clock functions', def: true },
	{ id: 'countdown', label: 'Countdown functions', def: true },
	{ id: 'message', label: 'Message and ticker', def: true },
	{ id: 'overlays', label: 'Overlays — logo, PiP, weather, every overlay off', def: true },
	{ id: 'vogs', label: 'All VOGs', def: true },
	{ id: 'stingers', label: 'All stingers', def: true },
	{ id: 'lower_thirds', label: 'All lower thirds — designs, preview, take, update', def: true },
	{ id: 'people', label: 'All people — the library', def: true },
	{ id: 'screens', label: 'Screens and canvases', def: true },
	{ id: 'audio', label: 'Audio playlist, break music, playlist parts', def: true },
	{ id: 'presenter', label: 'Presenter — steps, decks, web pages, the VT clock', def: true },
	{ id: 'install', label: 'Install — schedule, announcements, adverts', def: false },
	{ id: 'stage', label: 'Stage — the speaker timer, messages to the stage', def: true },
	{ id: 'nodes', label: 'Nodes and the twin — every other Patterns on the network', def: true },
]

/** The group a preset category belongs to. */
export function groupOf(category) {
	const c = String(category ?? '')
	if (c.startsWith('Transport') || c.startsWith('Stream')) return 'transport'
	if (c.startsWith('Cue') || c.startsWith('Upcoming cues')) return 'cues'
	if (c.startsWith('Look')) return 'looks'
	if (c.startsWith('Pattern')) return 'patterns'
	if (c.startsWith('Clock')) return 'clock'
	if (c.startsWith('Countdown')) return 'countdown'
	if (c.startsWith('Message')) return 'message'
	if (c.startsWith('Overlays')) return 'overlays'
	if (c.startsWith('VOG')) return 'vogs'
	if (c.startsWith('Stinger')) return 'stingers'
	if (c.startsWith('Lower third')) return 'lower_thirds'
	if (c.startsWith('People')) return 'people'
	if (c.startsWith('Screen')) return 'screens'
	if (c.startsWith('Audio') || c.startsWith('Break music') || c.startsWith('Playlist part')) return 'audio'
	if (c.startsWith('Presenter') || c.startsWith('Web page')) return 'presenter'
	if (c.startsWith('Install')) return 'install'
	if (c.startsWith('Stage')) return 'stage'
	if (c.startsWith('Node') || c.startsWith('Twin')) return 'nodes'
	return 'transport'
}

/** Ticked in the settings, or the group's default when the setting has never been saved. */
export function groupEnabled(config, id) {
	const g = GROUPS.find((x) => x.id === id)
	const v = config?.[`g_${id}`]
	return v === undefined || v === null ? (g?.def ?? true) : !!v
}

/**
 * Where the desk is: the bonjour pick ("10.0.0.12:9697" — Companion writes the address and the
 * advertised port) when one is chosen, else the typed host and port. Null when nothing is set.
 */
export function connectionTarget(config) {
	const picked = String(config?.desk ?? '').trim()
	if (picked) {
		const at = picked.lastIndexOf(':')
		const host = at > 0 ? picked.slice(0, at) : picked
		const port = at > 0 ? Number(picked.slice(at + 1)) : DEFAULT_PORT
		return { host, port: Number.isFinite(port) && port > 0 ? port : DEFAULT_PORT, discovered: true }
	}
	const host = String(config?.host ?? '').trim()
	if (!host) return null
	const port = Number(config?.port)
	return { host, port: Number.isFinite(port) && port > 0 ? port : DEFAULT_PORT, discovered: false }
}

export function configFields() {
	return [
		{
			type: 'static-text', id: 'about', width: 12, label: 'Patterns',
			value: 'Pick the desk from the list — every Patterns on this network announces itself — or type its address. The wire is the Companion (TCP) port on the desk\'s Remote page, 9697 unless you changed it.',
		},
		{ type: 'bonjour-device', id: 'desk', label: 'Desk on the network', width: 12 },
		{ type: 'textinput', id: 'host', label: 'Patterns machine IP', width: 8, regex: Regex.IP, default: '127.0.0.1', isVisibleExpression: '!$(options:desk)' },
		{ type: 'number', id: 'port', label: 'Companion (TCP) port — Remote page in Patterns', width: 4, min: 1024, max: 65535, default: DEFAULT_PORT, isVisibleExpression: '!$(options:desk)' },
		{
			type: 'textinput', id: 'token', label: 'Pairing token — Remote page, TRUST (leave empty for a desk that asks for none)', width: 12, default: '',
			tooltip: 'A desk with a pairing token set runs a verb only from a connection that presented it; the module sends AUTH with this after HELLO. Dashes and case do not matter.',
		},
		{
			type: 'static-text', id: 'groups_info', width: 12, label: 'Preset groups',
			value: 'Tick the groups of keys you want under Presets. Every group labels its keys from the show that is loaded and lights them from the air; untick what this desk never uses and the list stays short. The actions, feedbacks and variables are always all there.',
		},
		...GROUPS.map((g) => ({ type: 'checkbox', id: `g_${g.id}`, label: g.label, width: 6, default: g.def })),
	]
}
