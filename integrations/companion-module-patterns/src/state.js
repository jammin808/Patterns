// What the desk's STATE means to the module, as pure functions: the show's lists by place (a bank
// key labels itself from these), the cue bank's places, the variable values, and a signature of the
// lists so the per-show presets are rebuilt only when the show changes. Nothing here touches
// Companion — it is the part the tests read straight.

/** A pattern kind as typed by a person against a pattern kind as the desk names it: "colour bars", "color_bars" and "ColorBars" are one key. */
export const norm = (v) => String(v ?? '').replace(/[\s_-]/g, '').toLowerCase()

/** The state before the desk has said anything, and after the link drops: no key stays lit. */
export const emptyState = () => ({ blackout: false, looks: [], screens: [], presenter: { index: -1, count: 0 } })

/** The device row whose stamp (a field holding an ISO time) is newest, or undefined when none has one. */
export function newestDevice(rows, stamp) {
	let best
	for (const d of rows ?? []) {
		if (!d?.[stamp]) continue
		if (!best || String(d[stamp]) > String(best[stamp])) best = d
	}
	return best
}

/** The name at a place in one of the show's lists, or '' when nothing is there. */
export function bankName(s, kind, n) {
	switch (kind) {
		case 'look': return s.looks?.[n - 1]?.name ?? ''
		case 'look_f': return s.looks?.find((l) => l.slot === n)?.name ?? ''
		case 'lt': return s.lowerThirds?.[n - 1]?.name ?? ''
		case 'person': return s.people?.[n - 1]?.name ?? ''
		case 'stinger': return s.stingers?.[n - 1]?.name ?? ''
		case 'music': return s.music?.items?.[n - 1]?.name ?? ''
		case 'track': return s.audio?.items?.[n - 1]?.name ?? ''
		case 'section': return s.sections?.[n - 1]?.name ?? ''
		case 'screen': return s.screens?.[n - 1]?.label ?? ''
		case 'cue': return upcoming(s)[n - 1]?.number ?? ''
		case 'node': return nodeLabel(s.nodes?.[n - 1])
		default: return ''
	}
}

/** The standby cue and the cues after it, in order — the cue bank's places 1… */
export function upcoming(s) {
	const c = s.cuestack ?? {}
	return [c.standby, ...(c.next ?? [])].filter(Boolean)
}

/** A node as a bank key reads it: "Caller\nFOH-CALL". */
export function nodeLabel(node) {
	if (!node) return ''
	const kind = { desk: 'Desk', caller: 'Caller', arcade: 'Arcade', timer: 'Timer' }[node.kind] ?? node.kind ?? ''
	return `${kind}\n${node.name ?? ''}`.trim()
}

/** How many places each bank has. */
export const BANK_SIZES = { look: 16, look_f: 12, lt: 8, person: 8, stinger: 8, screen: 8, track: 8, music: 6, section: 6, cue: 7, node: 8 }

/** Every bank variable from the state. */
export function bankVariables(s) {
	const vars = {}
	for (let n = 1; n <= BANK_SIZES.look; n++) vars[`look_${n}`] = bankName(s, 'look', n)
	for (let f = 1; f <= BANK_SIZES.look_f; f++) vars[`look_f${f}`] = bankName(s, 'look_f', f)
	for (let n = 1; n <= 8; n++) {
		vars[`lt_${n}`] = bankName(s, 'lt', n)
		vars[`person_${n}`] = bankName(s, 'person', n)
		vars[`stinger_${n}`] = bankName(s, 'stinger', n)
		vars[`screen_${n}`] = bankName(s, 'screen', n)
		vars[`track_${n}`] = bankName(s, 'track', n)
		vars[`screen_${n}_pattern`] = s.screens?.find((x) => x.n === n)?.pattern ?? ''
		vars[`screen_${n}_signal`] = s.screens?.find((x) => x.n === n)?.signal?.result ?? ''
		vars[`screen_${n}_group`] = s.screens?.find((x) => x.n === n)?.role ?? ''
		vars[`screen_${n}_audio`] = s.screens?.find((x) => x.n === n)?.audioOutLabel ?? ''
		const node = s.nodes?.[n - 1]
		vars[`node_${n}`] = nodeLabel(node)
		vars[`node_${n}_kind`] = node?.kind ?? ''
		vars[`node_${n}_words`] = node?.words ?? ''
	}
	for (let n = 1; n <= 6; n++) {
		vars[`music_${n}`] = bankName(s, 'music', n)
		vars[`section_${n}`] = bankName(s, 'section', n)
	}
	const up = upcoming(s)
	for (let k = 1; k <= BANK_SIZES.cue; k++) {
		const row = up[k - 1]
		vars[`cue_${k}`] = row ? `${row.number ?? ''}\n${row.name ?? ''}`.trim() : ''
		vars[`cue_${k}_number`] = row?.number ?? ''
		vars[`cue_${k}_name`] = row?.name ?? ''
	}
	return vars
}

/** The show's lists as one string: when it changes, the per-show presets are built again. */
export function showSignature(s) {
	const names = (list) => (list ?? []).map((x) => x.name ?? x.label ?? '').join('|')
	return [
		names(s.looks), names(s.lowerThirds), names(s.people), (s.stingers ?? []).map((x) => `${x.kind}:${x.name}`).join('|'),
		names(s.music?.items), names(s.audio?.items), names(s.sections), names(s.screens), upcoming(s).map((c) => `${c.number} ${c.name}`).join('|'),
		(s.patternKinds ?? []).join('|'), (s.nodes ?? []).map((n) => `${n.kind}:${n.name}`).join('|'),
	].join('#')
}

/** The stage timer's colour word from the desk (green, amber, red) — anything else reads as off. */
export function stagePhase(s) {
	const t = s.stage?.timer
	if (!t) return 'off'
	return t.phase ?? 'off'
}

/** Every variable value from one STATE. Missing blocks read as their defaults, so an older desk never breaks a key. */
export function variableValues(s) {
	const p = s.presenter ?? { index: -1, count: 0 }
	const c = s.cuestack ?? {}
	const ov = s.overlays ?? {}
	const twin = s.twin ?? {}
	const stage = s.stage ?? {}
	const nodes = s.nodes ?? []
	return {
		program: s.airLabel ?? '',
		cue_armed: c.armed ? 'ARMED' : 'off',
		cue_hold: c.hold ? 'HOLD' : 'off',
		cue_seq: String(c.seq ?? 0),
		cue_standby_number: c.standby?.number ?? '-',
		cue_standby_name: c.standby?.name ?? '',
		cue_next_number: c.next?.[0]?.number ?? '-',
		cue_next_name: c.next?.[0]?.name ?? '',
		cue_previous_number: c.previous?.number ?? '-',
		cue_previous_name: c.previous?.name ?? '',
		cue_last_outcome: c.last?.outcome ?? '',
		cue_last_pending: String(c.last?.pending ?? 0),
		cue_confirm: c.confirm ?? '',
		blackout: s.blackout ? 'ON' : 'off',
		presenter_step: p.index >= 0 ? String(p.index + 1) : '-',
		presenter_count: String(p.count ?? 0),
		playlist: s.playlist ?? '',
		next_cue: s.nextCue ?? '',
		stinger: s.stingerPlaying ?? '',
		sting_hold: s.stingHold ?? '',
		duck: s.duck ? 'DUCK' : 'off',
		lower_third: s.lowerThird ?? '',
		lower_third_person: s.lowerThirdPerson ?? '',
		lower_third_preview: s.lowerThirdPreview ?? '',
		lower_third_preview_person: s.lowerThirdPreviewPerson ?? '',
		lower_third_default: s.lowerThirdDefault ?? '',
		lower_third_edited: s.lowerThirdEdited ? 'EDITED' : 'off',
		look_state: (s.airLook ?? '') === '' ? 'off' : s.lookEdited ? 'EDITED' : 'LIVE',
		look_screens_off: String(s.lookScreensOff ?? 0),
		stream_status: s.stream?.status ?? '',
		stream_health: s.stream?.health ?? '',
		stream_up: s.stream?.up ?? '',
		stream_fps: s.stream?.fps == null ? '' : String(s.stream.fps),
		outputs_live: s.live ? 'LIVE' : 'off',
		edit_safe: s.editSafe ? 'ON' : 'off',
		tone: s.tone ? 'ON' : 'off',
		timing_offset: c.timing?.offset ?? '',
		timing_next_break: c.timing?.nextBreak ?? '',
		timing_end: c.timing?.end ?? '',
		deck_page: s.deck?.count ? String(s.deck.page) : '',
		deck_count: s.deck?.count ? String(s.deck.count) : '',
		deck_file: s.deck?.file ?? '',
		video_file: s.video?.file ?? '',
		video_tag: s.video?.tag ?? '',
		video_position: s.video?.positionText ?? '',
		video_length: s.video?.lengthText ?? '',
		video_remaining: s.video?.remainingText ?? '',
		video_remaining_seconds: String(s.video?.remaining ?? 0),
		video_text: s.video?.text ?? '',
		video_chip: s.video?.chip ?? '',
		video_call: s.video?.call ?? '',
		audio_track: s.audio?.track ?? '',
		audio_next: s.audio?.next ?? '',
		audio_n: s.audio?.count ? String(s.audio.n || 0) : '',
		audio_count: String(s.audio?.count ?? 0),
		audio_position: s.audio?.positionText ?? '',
		audio_remaining: s.audio?.remainingText ?? '',
		audio_state: s.audio?.playing ? 'PLAYING' : 'stopped',
		web_page: s.web?.page ?? '',
		web_title: s.web?.title ?? '',
		web_service: s.web?.service ?? '',
		web_fps: s.web?.fps ? String(s.web.fps) : '',
		web_path: s.web?.path?.words ?? '',
		web_smoothing: s.web?.path?.smoothing ?? '',
		web_latency: s.web?.path ? `${Math.round(s.web.path.latencyMs ?? 0)} ms` : '',
		web_underruns: String(s.web?.path?.underruns ?? 0),
		web_capture: s.web?.path?.capture ?? '',
		audio_routing: s.audioRouting?.on ? 'ON' : 'off',
		audio_routing_words: s.audioRouting?.words ?? '',
		audio_follow: s.audioRouting?.follow ? 'ON' : 'off',
		audio_follow_words: s.audioRouting?.followWords ?? '',
		web_vt: s.web?.arm?.words ?? (s.web?.player ? `${s.web.player.text}${s.web.player.paused ? ' · paused' : ' · playing'}${s.web.player.ad ? ' · ADVERT' : ''}` : ''),
		web_armed: s.webArmed?.short ?? '',
		web_armed_page: s.webArmed?.page ?? '',
		review: s.review ? 'ON' : 'off',
		weather: s.weather?.on ? 'ON' : 'off',
		weather_text: s.weather?.text ?? '',
		weather_place: s.weather?.place ?? '',
		weather_figure: s.weather?.figure ?? '',
		weather_view: s.weather?.view ?? '',
		freeze: s.frozen ? 'FROZEN' : 'off',
		black: s.black?.count ? String(s.black.count) : 'off',
		black_text: s.black?.text ?? '',
		black_audio: s.black?.audio ? 'DOWN' : 'off',
		previous_look: s.previousLook ?? '',
		music: s.music?.now ?? '',
		music_state: s.music?.playing ? 'PLAYING' : 'paused',
		music_level: String(s.music?.level ?? 0),
		music_device: s.music?.device ?? '',
		health: s.health ?? '',
		machine_cpu: s.machine?.cpu >= 0 ? `${s.machine.cpu}%` : 'n/a',
		machine_fps: String(s.machine?.fps ?? 0),
		machine_power: s.machine?.battery ? 'BATTERY' : 'mains',
		machine_advice: String(s.machine?.advice ?? 0),
		machine_render_faults: String(s.machine?.renderFaults ?? 0),
		machine_faulting: s.machine?.faulting ? 'FAULT' : 'ok',
		machine_live_age: s.machine?.liveAgeMs >= 0 ? `${Math.round(s.machine.liveAgeMs)} ms` : 'n/a',
		machine_memory_pressure: s.memory?.pressure ?? 'none',
		machine_memory_held: s.memory?.residency?.words ?? '',
		machine_gpu_cache: s.machine?.gpuCache?.words ?? '',
		machine_inventory: s.machine?.inventory ?? '',
		machine_rig: s.machine?.rig ?? '',
		commissioning: s.commissioning?.headline ?? '',
		commissioning_next: s.commissioning?.next ?? '',
		eye_headline: s.eye?.headline ?? '',
		eye_worst: s.eye?.worstWords ?? '',
		eye_problems: String(s.eye?.problems ?? 0),
		eye_focus: s.eye?.focus ?? '',
		take_next: s.take?.next?.set ? (s.take.next.words ?? '') : '',
		take_scope: s.take?.scopeLabel ?? '',
		take_words: s.take?.refusal ? s.take.refusal : (s.take?.words ?? ''),
		take_landing: s.take?.landing?.words ?? '',
		editing_target: s.editing?.label ?? '',
		editing_kind: s.editing?.kind ?? '',
		editing_editor: s.editing?.editor ?? '',
		editing_library: s.editing?.library ?? '',
		inputs_pending: s.inputs?.pendingNote ?? '',
		devices_failing: String((s.devices ?? []).filter((d) => d.failing).length),
		device_last_reply: (() => {
			const d = newestDevice(s.devices, 'lastReplyUtc')
			return d ? `${d.name}: ${d.lastReply}${d.confirmed ? ' — ' + d.confirmed : ''}` : ''
		})(),
		device_last_failure: (() => {
			const d = newestDevice(s.devices, 'lastFailureUtc')
			return d ? `${d.name}: ${d.lastFailure}` : ''
		})(),
		air_look: s.airLook ?? '',
		preview_look: s.previewLook ?? '',
		pattern: s.pattern ?? '',
		install: s.install?.on ? 'ON' : 'off',
		install_programme: s.install?.programme ?? '',
		install_over: s.install?.over ?? '',
		install_next: s.install?.next ?? '',
		install_status: s.install?.status ?? '',
		// The overlays a key drives: the clock, the message, the countdown, the logo, the PiP, and the line.
		clock: ov.clock?.on ? 'ON' : 'off',
		clock_hours: String(ov.clock?.hours ?? 24),
		clock_text: ov.clock?.text ?? '',
		clock_seconds: ov.clock?.seconds ? 'ON' : 'off',
		clock_date: ov.clock?.date ? 'ON' : 'off',
		message: ov.message?.on ? 'ON' : 'off',
		message_text: ov.message?.text ?? '',
		message_scroll: ov.message?.scroll ? 'SCROLL' : 'still',
		countdown: ov.countdown?.phase ?? 'off',
		countdown_text: ov.countdown?.text ?? '',
		countdown_remaining_seconds: String(ov.countdown?.remaining ?? 0),
		countdown_label: ov.countdown?.label ?? '',
		countdown_target: ov.countdown?.target ?? '',
		logo: ov.logo?.on ? 'ON' : 'off',
		pip: ov.pip?.on ? 'ON' : 'off',
		overlays_text: ov.text ?? '',
		// The desk itself: its version, the show, and what it says of the link.
		desk_version: s.version ?? '',
		show: s.show ?? '',
		// The nodes around the desk, the twin, and the stage.
		nodes_count: String(nodes.filter((n) => n.fresh !== false).length),
		nodes_text: nodes.filter((n) => n.fresh !== false).map((n) => nodeLabel(n).replace('\n', ' ')).join(' · '),
		twin_role: twin.role ?? 'off',
		twin_phase: twin.phase ?? '',
		twin_words: twin.words ?? '',
		twin_holder: twin.holder ?? '',
		twin_clocks: twin.clocks ?? '',
		stage_timer: stage.timer?.text ?? '',
		stage_phase: stage.timer?.phase ?? 'off',
		stage_colour: stage.timer?.colour ?? '',
		stage_label: stage.timer?.label ?? '',
		stage_remaining_seconds: String(Math.round(stage.timer?.remaining ?? 0)),
		stage_progress: String(Math.round((stage.timer?.progress ?? 0) * 100)),
		stage_segment: stage.segment ?? '',
		stage_next: stage.next ?? '',
		stage_pending: stage.pendingSpeaker ?? '',
		stage_pending_crew: stage.pendingCrew ?? '',
		...bankVariables(s),
	}
}
