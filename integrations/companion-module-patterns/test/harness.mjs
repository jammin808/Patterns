// A Patterns instance booted against the real @companion-module/base, with a fake host context
// that records what the module hands Companion, and a fake wire that records what it sends.
import { EventEmitter } from 'node:events'
import { readFileSync } from 'node:fs'
import { InstanceStatus } from '@companion-module/base'
import PatternsInstance from '../src/main.js'

// The base's own logger prints to stdout without a host; the tests and the line emitter want the wire alone.
global.COMPANION_LOGGER = () => {}

export class FakeSocket extends EventEmitter {
	constructor() {
		super()
		this.isConnected = true
		this.sent = []
		this.destroyed = false
	}
	send(msg) { this.sent.push(String(msg)); return true }
	destroy() { this.destroyed = true }
	/** The desk speaks: bytes on the wire, as the socket would emit them. */
	receive(text) { this.emit('data', Buffer.from(text, 'utf8')) }
}

/** The host's side of the module, as the base's InstanceBase needs it, with everything set recorded. */
export function fakeContext(label = 'FOH deck') {
	const ctx = {
		id: 'patterns-1',
		label,
		_isInstanceContext: true,
		actions: {}, feedbacks: {}, variableDefs: {}, variables: {}, structure: [], presets: {}, statuses: [], checks: 0, checked: [], recorded: [],
		setActionDefinitions(a) { ctx.actions = a },
		setFeedbackDefinitions(f) { ctx.feedbacks = f },
		setVariableDefinitions(v) { ctx.variableDefs = v },
		setVariableValues(v) { Object.assign(ctx.variables, v) },
		setPresetDefinitions(structure, presets) { ctx.structure = structure; ctx.presets = presets },
		setCompositeElementDefinitions() {},
		checkAllFeedbacks() { ctx.checks++ },
		checkFeedbacks(...ids) { ctx.checked.push(...ids) },
		checkFeedbacksById() {},
		getVariableValue(id) { return ctx.variables[id] },
		updateStatus(status, message) { ctx.statuses.push({ status, message }) },
		saveConfig() {}, oscSend() {}, recordAction(action, uniquenessId) { ctx.recorded.push({ ...action, uniquenessId }) }, subscribeActions() {}, unsubscribeActions() {}, unsubscribeFeedbacks() {},
	}
	return ctx
}

export const sampleState = () => JSON.parse(readFileSync(new URL('./fixtures/state.json', import.meta.url), 'utf8'))

/** A booted instance on a fake wire, the desk's first STATE already heard unless told otherwise. */
export async function boot({ config = { host: '10.0.0.5', port: 9697 }, state = sampleState(), label = 'FOH deck' } = {}) {
	const ctx = fakeContext(label)
	const inst = new PatternsInstance(ctx)
	inst.log = () => {} // the base's logger wants a host; the tests read the wire instead
	const socket = new FakeSocket()
	inst.socketFactory = () => socket
	await inst.init(config, true, undefined)
	socket.emit('status_change', InstanceStatus.Ok, undefined)
	if (state) socket.receive('STATE ' + JSON.stringify(state) + '\n')
	return { inst, ctx, socket, lines: () => socket.sent.map((l) => l.replace(/\n$/, '')) }
}

/** An action pressed with these options: the lines it put on the wire. */
export function pressAction(booted, actionId, options) {
	const before = booted.socket.sent.length
	const def = booted.ctx.actions[actionId]
	if (!def) throw new Error(`No action ${actionId}`)
	def.callback({ id: 'a', controlId: 'c', actionId, options, surfaceId: undefined }, { signal: new AbortController().signal })
	return booted.socket.sent.slice(before).map((l) => l.replace(/\n$/, ''))
}

/** A feedback asked with these options. */
export function askFeedback(booted, feedbackId, options) {
	const def = booted.ctx.feedbacks[feedbackId]
	if (!def) throw new Error(`No feedback ${feedbackId}`)
	return def.callback({ type: 'boolean', id: 'f', controlId: 'c', feedbackId, options, previousOptions: null }, { signal: new AbortController().signal })
}
