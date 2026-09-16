// Bitfocus Companion module for Patterns — the desk's wire (one line per press, STATE pushed on
// every change) as Companion 5 keys: actions, feedbacks in the palette's colours, variables that
// label the banks, presets in sections, and the desk found on the network by itself.
import { InstanceBase, InstanceStatus, TCPHelper } from '@companion-module/base'
import { buildActions } from './actions.js'
import { buildFeedbacks } from './feedbacks.js'
import { buildPresets, buildShowPresets, structure } from './presets.js'
import { variableDefinitions } from './variables.js'
import { configFields, connectionTarget, groupEnabled, groupOf } from './config.js'
import { emptyState, showSignature, upcoming, variableValues } from './state.js'

/** The module's own version, said on HELLO so the desk's Remote page can show which module a deck runs. */
export const MODULE_VERSION = '3.12.0'

/** What the state carries that a feedback reads: every boolean feedback is rechecked on every STATE. */
class PatternsInstance extends InstanceBase {
	constructor(internal) {
		super(internal)
		this.socket = null
		this.state = emptyState()
		this.buffer = ''
		this.standbyId = '' // the standby id this instance last saw — every cue_go sends it
		this.showSignature = '' // the show's lists as last turned into presets — rebuilt only when they change
		this.fixed = null // the fixed presets, built once
		/** How the wire is opened — a test hands in a fake; the desk gets a TCPHelper. */
		this.socketFactory = (host, port) => new TCPHelper(host, port)
	}

	async init(config) {
		this.config = config
		this.updateStatus(InstanceStatus.Connecting)
		const ctx = {
			send: (line) => this.send(line),
			log: (level, text) => this.log(level, text),
			state: () => this.state,
			standbyId: () => this.standbyId,
			upcoming: () => upcoming(this.state),
		}
		this.setActionDefinitions(buildActions(ctx))
		this.setFeedbackDefinitions(buildFeedbacks(ctx))
		this.setVariableDefinitions(variableDefinitions())
		this.setVariableValues(variableValues(this.state))
		this.refreshPresets()
		this.initSocket()
	}

	async destroy() {
		if (this.socket) {
			this.socket.destroy()
			this.socket = null
		}
	}

	async configUpdated(config) {
		this.config = config
		this.initSocket()
		this.refreshPresets() // the ticked groups may have changed
	}

	getConfigFields() {
		return configFields()
	}

	// ---- presets: the fixed ones and the show's own, through the ticked groups --------------------------------

	/** The fixed presets and the show's own, in their sections, through the ticked groups. */
	refreshPresets() {
		this.fixed ??= buildPresets()
		const show = buildShowPresets(this.state)
		const presets = { ...this.fixed.presets, ...show.presets }
		const categories = { ...this.fixed.categories, ...show.categories }
		const sections = structure(categories, (category) => groupEnabled(this.config, groupOf(category)))
		const kept = {}
		for (const section of sections) for (const id of section.definitions) kept[id] = presets[id]
		this.setPresetDefinitions(sections, kept)
	}

	/** Presets built from the show itself, rebuilt when the lists change. */
	refreshShowPresets() {
		const signature = showSignature(this.state)
		if (signature === this.showSignature) return
		this.showSignature = signature
		this.refreshPresets()
	}

	// ---- the wire -----------------------------------------------------------------------------------------------

	initSocket() {
		if (this.socket) {
			this.socket.destroy()
			this.socket = null
		}
		const target = connectionTarget(this.config)
		if (!target) {
			this.updateStatus(InstanceStatus.BadConfig, 'Pick the desk on the network, or type its address')
			return
		}
		this.buffer = ''
		this.socket = this.socketFactory(target.host, target.port)
		this.socket.on('status_change', (status, message) => {
			this.updateStatus(status, message)
			if (status === InstanceStatus.Ok) {
				// The connection names itself and its module, so the caller's history reads "GO from FOH deck"
				// and the desk's Remote page can say which module version each deck runs.
				this.socket.send(`HELLO ${this.label ?? 'Companion'} module=${MODULE_VERSION}\n`)
				// A desk with a pairing token (Remote page, TRUST) runs a verb only from a connection that
				// presented it: AUTH follows HELLO with the token from the config, and nothing is sent without one.
				const token = pairingToken(this.config)
				if (token) this.socket.send(`AUTH ${token}\n`)
			} else {
				// Feedbacks reset on disconnect: a dead key must not stay green.
				this.state = emptyState()
				this.standbyId = ''
				this.setVariableValues(variableValues(this.state))
				this.checkAllFeedbacks()
			}
		})
		this.socket.on('error', (err) => this.log('error', `Connection error: ${err.message}`))
		this.socket.on('data', (data) => this.onData(data.toString('utf8')))
	}

	/** Bytes from the desk: whole lines, STATE and ERR read, the rest (OK …) let by. */
	onData(text) {
		this.buffer += text
		let idx
		while ((idx = this.buffer.indexOf('\n')) >= 0) {
			const line = this.buffer.slice(0, idx).trim()
			this.buffer = this.buffer.slice(idx + 1)
			this.onLine(line)
		}
		if (this.buffer.length > 4_000_000) this.buffer = '' // a line that never ends is not the desk's
	}

	onLine(line) {
		if (line.startsWith('STATE ')) this.onState(line.slice(6))
		else if (line.startsWith('ERR')) {
			this.log('warn', line)
			this.setVariableValues({ last_error: line.slice(4).trim() })
			const trust = trustProblem(line)
			if (trust) this.updateStatus(InstanceStatus.BadConfig, trust) // the connection is up, the desk will not run a verb from it: the config is what needs a hand
		}
	}

	onState(json) {
		let parsed
		try {
			parsed = JSON.parse(json)
		} catch (e) {
			this.log('warn', `Bad STATE payload: ${e.message}`)
			return
		}
		this.state = parsed && typeof parsed === 'object' ? parsed : emptyState()
		this.standbyId = this.state.cuestack?.standby?.id ?? ''
		this.setVariableValues(variableValues(this.state))
		this.checkAllFeedbacks()
		this.refreshShowPresets()
	}

	send(cmd) {
		if (this.socket?.isConnected) this.socket.send(cmd + '\n')
		else this.log('warn', `Not connected — dropped: ${cmd}`)
	}
}

export default PatternsInstance

/** The pairing token as the wire carries it: trimmed, or '' when the config has none. */
export function pairingToken(config) {
	return String(config?.token ?? '').trim()
}

/**
 * The words for Companion's status when the desk refuses the connection's verbs over trust: the
 * token is wrong, or the desk asks for one and the config has none. Null for every other ERR.
 */
export function trustProblem(errLine) {
	const text = String(errLine ?? '')
	if (/^ERR\s+wrong token/i.test(text)) return "The pairing token is not this desk's — Remote page, TRUST, on the desk"
	if (/^ERR\s+not paired/i.test(text)) return 'This desk asks for its pairing token — type it into this connection (Remote page, TRUST, on the desk)'
	return null
}

/** Upgrade scripts, oldest first; none yet — every id of 1.x and 2.x is kept as it was. */
export const UpgradeScripts = []
