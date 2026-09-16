// The presets: keys ready to drag, in sections Companion 5 shows by name. Every key labels itself
// from the show through the variables and lights from the air through the feedbacks; the banks
// (a look, a cue, a screen, a node by its place) fill themselves as the show is built. The colours
// are the palette's, so a key of a kind wears the kind's hue and the state's treatment.
//
// buildPresets(state) returns { presets, categories }: the definitions by id and each id's category
// (the section it sits in). structure(categories, enabled) turns the categories into the sections.
import { COLOURS, rgb, idle, on, style, empty as emptyStyle } from './palette.js'
import { upcoming } from './state.js'

const white = rgb(COLOURS.white)
const dark = rgb(COLOURS.dark)
const ink = rgb(COLOURS.ink)

/** A simple key: text, a size, the idle ground, its press and its lights. */
function key(category, name, text, size, down, feedbacks = [], extra = {}) {
	return { category, preset: { type: 'simple', name, style: { text, size, ...idle(), ...extra }, steps: [{ down, up: [] }], feedbacks } }
}
const press = (actionId, options = {}) => [{ actionId, options }]
const lit = (feedbackId, options, styleName) => ({ feedbackId, options, style: on(styleName) })
const litAs = (feedbackId, options, kind, state) => ({ feedbackId, options, style: style(kind, state) })
/** The bank key with nothing behind it dims. */
const empty = (kind, n) => ({ feedbackId: 'slot_empty', options: { kind, n }, style: emptyStyle() })

/** The section order — the order a person reads the list in. */
export const CATEGORY_ORDER = [
	'Cue stack', 'Cue bank (labels itself)', 'Upcoming cues — this show', 'Transport', 'Take', 'Stream',
	'Look bank (labels itself)', 'Looks', 'Looks — this show', 'Patterns — every kind',
	'Screens', 'Screens — this show', 'Presenter', 'Web page',
	'Lower thirds', 'Lower thirds — this show', 'People', 'People — this show',
	'Stingers', 'Stingers — this show', 'VOG', 'VOGs — this show',
	'Audio', 'Audio playlist — this show', 'Break music', 'Break music — this show', 'Playlist parts', 'Playlist parts — this show',
	'Clock', 'Countdown', 'Message', 'Overlays', 'Stage', 'Nodes', 'Twin', 'Install', 'Eye',
]

const slug = (s) => String(s).toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '')

/** The sections Companion shows: one per category, in reading order, holding the ids of the presets kept. */
export function structure(categories, keep = () => true) {
	const byCategory = new Map()
	for (const [id, category] of Object.entries(categories)) {
		if (!keep(category)) continue
		if (!byCategory.has(category)) byCategory.set(category, [])
		byCategory.get(category).push(id)
	}
	const order = [...CATEGORY_ORDER, ...[...byCategory.keys()].filter((c) => !CATEGORY_ORDER.includes(c))]
	return order.filter((c) => byCategory.has(c)).map((c) => ({ id: slug(c), name: c, definitions: byCategory.get(c) }))
}

/** The fixed presets, built once. */
export function buildPresets() {
	const out = {}
	const add = (id, built) => { out[id] = built }

	// THE LOOK'S OWN KEY, in three states: green up and untouched, amber up but changed since, dark not up.
	add('look_state', key('Looks', 'The look on air — live, changed since, or not up', 'LOOK\n$(patterns:air_look)\n$(patterns:look_state)', 'auto', press('look_back'), [
		litAs('look_on_air', { name: '' }, 'look', 'air'), litAs('look_edited', { name: '' }, 'look', 'edited'), litAs('look_screens_off', {}, 'look', 'screensOff'),
	]))
	add('stream_health', key('Stream', 'The stream — live, and whether it is happy', 'STREAM\n$(patterns:stream_health)\n$(patterns:stream_up)', 'auto', press('stream', { mode: 'ON' }), [
		litAs('stream_active', {}, 'stream', 'active'), litAs('stream_trouble', {}, 'stream', 'trouble'),
	]))
	add('timing', key('Cue stack', 'How the day is running (and the next break)', '$(patterns:timing_offset)\n▸ $(patterns:timing_next_break)', 'auto', [], [litAs('running_late', { minutes: 5 }, 'cue', 'late')]))
	add('plan_minus', key('Cue stack', 'PLAN −1 MIN — the day slips a minute earlier', 'PLAN\n−1 MIN', '14', press('plan', { mode: 'SHIFT', delta: '-1:00' })))
	add('plan_plus', key('Cue stack', 'PLAN +1 MIN — the day slips a minute later', 'PLAN\n+1 MIN', '14', press('plan', { mode: 'SHIFT', delta: '+1:00' })))
	add('plan_resume', key('Cue stack', 'RESUME NOW — the standby cue starts the clock', 'RESUME\nNOW', '14', press('plan', { mode: 'RESUME', delta: '' })))
	add('plan_catchup', key('Cue stack', 'CATCH UP — the lateness made up before the next break', 'CATCH\nUP', '14', press('plan', { mode: 'CATCHUP', delta: '' }), [litAs('running_late', { minutes: 1 }, 'cue', 'late')]))
	add('edit_safe', key('Transport', 'EDIT SAFE — is there a preview and a TAKE to come', 'EDIT\nSAFE\n$(patterns:edit_safe)', 'auto', [], [litAs('edit_safe', {}, 'transport', 'editSafe')]))
	add('outputs_live', key('Transport', 'The outputs — open or not', 'OUTPUTS\n$(patterns:outputs_live)', 'auto', press('go'), [litAs('outputs_live', {}, 'look', 'air')]))

	// Banks: keys that label themselves from the show — drag a row once and every look, design,
	// person, stinger, track, part, screen, node or upcoming cue made later appears on the next key.
	for (let n = 1; n <= 16; n++) {
		add(`look_bank_${n}`, key('Look bank (labels itself)', `Look bank ${n} — the look at place ${n} in the show's list`, `$(patterns:look_${n})`, 'auto', press('look_bank', { n }), [
			// Three states on one key, and the order is the point: the amber "changed since" sits after the green "on air" and wins when both are true.
			litAs('look_bank_on_air', { n }, 'look', 'air'), litAs('look_bank_edited', { n }, 'look', 'edited'), empty('look', n),
		]))
	}
	for (let k = 1; k <= 7; k++) {
		add(`cue_bank_${k}`, key('Cue bank (labels itself)', k === 1 ? 'Cue bank 1 — the standby cue' : `Cue bank ${k} — the cue ${k - 1} after the standby: press to put it on standby`,
			`$(patterns:cue_${k})`, 'auto', press('cue_bank', { k, mode: 'STANDBY' }), [empty('cue', k)], k === 1 ? { bgcolor: rgb(COLOURS.green) } : {}))
	}
	for (let n = 1; n <= 6; n++) {
		add(`section_${n}`, key('Playlist parts', `Playlist part ${n} on air`, `PART ${n}\n$(patterns:section_${n})`, 'auto', press('section', { n }), [empty('section', n)]))
	}

	add('go', key('Transport', 'Outputs on', 'OUTPUTS\nON', '14', press('go'), [], { bgcolor: rgb(COLOURS.outputsOn) }))
	// Round 67 — the next TAKE: what it will do (the desk's plan, or why it is refused) and the one-shot it arrives by.
	add('take_plan', key('Take', "NEXT TAKE — the desk's plan for the next take (the scope and what it changes), or why it is refused", '$(patterns:take_scope)\n$(patterns:take_words)', 'auto', [], []))
	add('take_next_clear', key('Take', "NEXT: the show's own transition again (CLEAR)", 'NEXT\n$(patterns:take_next)', 'auto', press('take_next', { kind: 'CLEAR', ms: 0, sting: '' }), [litAs('take_next_set', {}, 'take', 'next')]))
	for (const [kind, label] of [['cut', 'CUT'], ['dissolve', 'DISSOLVE'], ['dip', 'DIP'], ['wipe', 'WIPE'], ['push', 'PUSH'], ['brand', 'BRAND\nSTINGER']]) {
		add(`take_next_${slug(kind)}`, key('Take', `NEXT: ${label.replace('\n', ' ')} — the next TAKE alone arrives this way`, `NEXT\n${label}`, 'auto', press('take_next', { kind, ms: 0, sting: '' }), [litAs('take_next_set', {}, 'take', 'next')]))
	}
	add('take_next_sting_1', key('Take', 'NEXT: STING 1 — the next TAKE arrives under the first video sting of the library', 'NEXT\nSTING 1', 'auto', press('take_next', { kind: 'STING', ms: 0, sting: '1' }), [litAs('take_next_sting', {}, 'take', 'sting')]))
	add('stop', key('Transport', 'Outputs off', 'OUTPUTS\nOFF', '14', press('stop'), [], { bgcolor: rgb(COLOURS.off) }))
	add('blackout', key('Transport', 'Blackout toggle', 'BLACK\nOUT', '18', press('blackout', { mode: 'TOGGLE' }), [litAs('blackout', {}, 'transport', 'blackout')]))
	add('cue_go', key('Cue stack', 'GO', 'GO\n$(patterns:cue_standby_number)\n$(patterns:cue_standby_name)', '14', press('cue_go'), [
		litAs('cue_armed', {}, 'cue', 'armed'), litAs('cue_hold', {}, 'cue', 'hold'),
		{ feedbackId: 'cue_confirm_required', options: {}, style: { ...style('cue', 'confirm'), text: '$(patterns:cue_confirm)' } },
		litAs('cue_last_failed', {}, 'cue', 'failed'),
	]))
	add('cue_standby_next', key('Cue stack', 'Standby next', 'STANDBY\n▼', '14', press('cue_standby', { mode: 'NEXT', cue: '' })))
	add('cue_standby_prev', key('Cue stack', 'Standby previous', 'STANDBY\n▲', '14', press('cue_standby', { mode: 'PREV', cue: '' })))
	add('cue_hold', key('Cue stack', 'HOLD', 'HOLD', '18', press('cue_hold', { mode: 'TOGGLE' }), [litAs('cue_hold', {}, 'cue', 'hold')]))
	add('cue_arm', key('Cue stack', 'ARM', 'ARM\n$(patterns:cue_armed)', '14', press('cue_arm', { mode: 'ON' }), [litAs('cue_armed', {}, 'cue', 'hold')]))
	add('stop_all', key('Cue stack', 'STOP ALL', 'STOP\nALL', '14', press('stop_all'), [], { color: rgb(COLOURS.red) }))
	add('duck', key('Cue stack', 'DUCK (live announcement)', 'DUCK\n$(patterns:duck)', '14', press('duck', { mode: 'TOGGLE' }), [litAs('duck_on', {}, 'duck', 'on')]))
	const lowerOn = litAs('lower_third_on', { name: '' }, 'lowerThird', 'on')
	add('review', key('Transport', 'REVIEW — the preview on every multiview', 'REVIEW\n$(patterns:review)', '14', press('review', { mode: 'TOGGLE' }), [litAs('review_on', {}, 'transport', 'review')]))
	// The weather chip: a key that reads the figure and the place, lit while the chip is on air; a second key turns it to tomorrow.
	add('weather', key('Overlays', 'WEATHER — the chip on air (reads the figure)', '$(patterns:weather_figure)\n$(patterns:weather_place)', '14', press('weather', { mode: 'TOGGLE' }), [litAs('weather_on', {}, 'weather', 'on')]))
	add('weather_tomorrow', key('Overlays', 'WEATHER — tomorrow', 'WEATHER\nTOMORROW', '14', press('weather', { mode: 'TOMORROW' })))
	// The clock from keys: every key reads the air and lights while its overlay is on.
	const sky = (id, options = {}) => litAs(id, options, 'overlay', 'on')
	add('clock', key('Clock', 'CLOCK — the clock overlay on air (reads the time)', 'CLOCK\n$(patterns:clock_text)', '14', press('clock', { mode: 'TOGGLE' }), [sky('clock_on')]))
	for (const hours of [12, 24]) add(`clock_${hours}h`, key('Clock', `CLOCK — ${hours}-hour`, `CLOCK\n${hours} H`, '14', press('clock', { mode: String(hours) }), [sky('clock_hours', { hours })]))
	add('clock_seconds', key('Clock', 'CLOCK — seconds shown or not', 'CLOCK\nSECONDS', '14', press('clock', { mode: 'SECONDS TOGGLE' }), [sky('clock_seconds')]))
	add('clock_date', key('Clock', 'CLOCK — the date line shown or not', 'CLOCK\nDATE', '14', press('clock', { mode: 'DATE TOGGLE' }), [sky('clock_date')]))
	add('clock_off', key('Clock', 'CLOCK — off', 'CLOCK\nOFF', '14', press('clock', { mode: 'OFF' }), [], { bgcolor: rgb(COLOURS.off) }))
	// The countdown: a key that reads what is left (green while it runs, red when it is over), the quick minutes, a time of day, the label, stop.
	const counting = litAs('countdown_running', { phase: 'running' }, 'countdown', 'running')
	const over = litAs('countdown_running', { phase: 'over' }, 'countdown', 'over')
	const cd = (mode, minutes = '', time = '', label = '') => press('countdown', { mode, minutes, time, label })
	add('countdown_clock', key('Countdown', 'COUNTDOWN — what is left (green while running, red when over); press: on as set up on the desk, press again: off', '$(patterns:countdown_text)', '14', cd('TOGGLE'), [counting, over]))
	for (const m of [1, 5, 10, 15, 30]) add(`countdown_${m}`, key('Countdown', `COUNTDOWN — ${m} min from now`, `⏱\n${m} MIN`, '14', cd('START', String(m)), [counting, over]))
	add('countdown_to', key('Countdown', 'COUNTDOWN — to a time of day (edit the time)', '⏱ TO\n19:30', '14', cd('TO', '', '19:30'), [counting, over]))
	add('countdown_label', key('Countdown', 'COUNTDOWN — the label over the digits (edit the words)', 'LABEL\nSHOW STARTS IN', '14', cd('LABEL', '', '', 'SHOW STARTS IN')))
	add('countdown_stop', key('Countdown', 'COUNTDOWN — stop', '⏱\nSTOP', '14', cd('STOP'), [counting, over], { bgcolor: rgb(COLOURS.off) }))
	add('countdown_follow', key('Countdown', 'COUNTDOWN FOLLOW — the countdown follows the running order (the standby cue\'s planned start)', '⏱ FOLLOW\nTHE PLAN', '14', press('countdown_follow', { mode: 'ON' }), [counting, over]))
	// The message: a key that reads the words on screen and toggles them, these words (edit them), the ticker, off.
	add('message', key('Message', 'MESSAGE — the message overlay on air (reads the words)', 'MSG\n$(patterns:message_text)', 'auto', press('message', { mode: 'TOGGLE', text: '' }), [sky('message_on')]))
	add('message_say', key('Message', 'MESSAGE — these words on screen (edit them)', 'SAY\nDoors open at 7', '14', press('message', { mode: 'SAY', text: 'Doors open at 7' }), [sky('message_on')]))
	add('message_scroll', key('Message', 'MESSAGE — scroll as a ticker, or stand still', 'MSG\nSCROLL', '14', press('message', { mode: 'SCROLL TOGGLE', text: '' }), [sky('message_scroll')]))
	add('message_off', key('Message', 'MESSAGE — off (the words kept)', 'MSG\nOFF', '14', press('message', { mode: 'OFF', text: '' }), [], { bgcolor: rgb(COLOURS.off) }))
	// The overlays: the logo, the PiP, and every overlay off in one press (the key reads what is on).
	add('logo', key('Overlays', 'LOGO — the brand logo overlay', 'LOGO', '18', press('logo', { mode: 'TOGGLE' }), [sky('logo_on')]))
	add('pip', key('Overlays', 'PIP — the picture-in-picture inset', 'PIP', '18', press('pip', { mode: 'TOGGLE' }), [sky('pip_on')]))
	add('overlays_off', key('Overlays', 'OVERLAYS OFF — the clock, the message, the countdown, the logo, the PiP and the weather chip all off (reads what is on)', 'OVERLAYS\nOFF\n$(patterns:overlays_text)', 'auto', press('overlays_off'), [], { bgcolor: rgb(COLOURS.off) }))
	add('freeze', key('Transport', 'FREEZE — every output holds its frame', 'FREEZE\n$(patterns:freeze)', '14', press('freeze', { mode: 'TOGGLE' }), [litAs('frozen', {}, 'transport', 'frozen')]))
	const fade = (dir, secs, target = '') => press('fade', { dir, secs, target })
	add('fade_down', key('Transport', 'FADE TO BLACK — 2 s', 'FADE\nTO BLACK\n2 s', '14', fade('DOWN', 2), [litAs('blackout', {}, 'transport', 'black')]))
	add('fade_up', key('Transport', 'FADE UP — 2 s', 'FADE\nUP\n2 s', '14', fade('UP', 2)))
	add('fade_focused_down', key('Transport', 'FADE TO BLACK — the focused screen, 2 s', 'FADE\nFOCUSED\n▼', '14', fade('DOWN', 2, 'FOCUSED'), [litAs('black_any', {}, 'transport', 'black')]))
	add('fade_focused_up', key('Transport', 'FADE UP — the focused screen, 2 s', 'FADE\nFOCUSED\n▲', '14', fade('UP', 2, 'FOCUSED')))
	add('fade_ticked_down', key('Transport', 'FADE TO BLACK — the ticked screens, 2 s', 'FADE\nTICKED\n▼', '14', fade('DOWN', 2, 'TICKED'), [litAs('black_any', {}, 'transport', 'black')]))
	add('fade_ticked_up', key('Transport', 'FADE UP — the ticked screens, 2 s', 'FADE\nTICKED\n▲', '14', fade('UP', 2, 'TICKED')))
	add('look_back', key('Looks', 'PREVIOUS LOOK — back on air', 'BACK TO\n$(patterns:previous_look)', '14', press('look_back')))
	add('lower_third_off', key('Lower thirds', 'Lower third off', 'LT\nOFF\n$(patterns:lower_third)', '14', press('lower_third_off'), [lowerOn]))
	for (let n = 1; n <= 6; n++) {
		add(`lower_third_${n}`, key('Lower thirds', `Lower third ${n} (labels itself)`, `LT ${n}\n$(patterns:lt_${n})`, 'auto', press('lower_third', { n }), [lowerOn, empty('lt', n)]))
		add(`person_${n}`, key('People', `Person ${n} (library) into the lower third on air (labels itself)`, `$(patterns:person_${n})`, 'auto', press('lower_third_person', { n, design: '' }), [lowerOn, empty('person', n)]))
	}
	const lowerPvw = litAs('lower_third_preview', { name: '' }, 'lowerThird', 'preview')
	add('lower_third_take', key('Lower thirds', 'Lower third TAKE — the one in the preview to air', 'LT TAKE\n$(patterns:lower_third_preview)', '14', press('lower_third_take'), [lowerPvw]))
	add('lower_third_update', key('Lower thirds', 'Lower third UPDATE — push an edit to the design on air', 'LT\nUPDATE', '14', press('lower_third_update'), [litAs('lower_third_edited', {}, 'lowerThird', 'edited')]))
	add('lower_third_preview_off', key('Lower thirds', 'Lower third preview clear', 'LT PVW\nCLEAR', '14', press('lower_third_preview_off'), [lowerPvw]))
	for (let n = 1; n <= 6; n++) {
		add(`lower_third_preview_${n}`, key('Lower thirds', `Lower third ${n} to preview (sign-off)`, `LT PVW\n${n}`, '14', press('lower_third_preview', { n, person: '' }), [lowerPvw]))
		add(`person_preview_${n}`, key('People', `Person ${n} (library) to preview, into the design in the preview, on air, or the default`, `PVW\nPERSON ${n}`, '14', press('lower_third_preview', { n: 0, person: String(n) }), [lowerPvw]))
	}
	const deckOn = litAs('deck_on_air', { ended: false }, 'presenter', 'on')
	const deckEnded = litAs('deck_on_air', { ended: true }, 'presenter', 'ended')
	for (const [id, text, mode] of [['next', 'DECK\n▶ $(patterns:deck_page)/$(patterns:deck_count)', 'NEXT'], ['prev', 'DECK\n◀', 'PREV'], ['first', 'DECK\nFIRST', 'FIRST'], ['last', 'DECK\nLAST', 'LAST']]) {
		add(`deck_${id}`, key('Presenter', `Deck — ${mode.toLowerCase()} page`, text, '14', press('deck_page', { mode, n: 1 }), id === 'next' ? [deckOn, deckEnded] : [deckOn]))
	}
	// The caller's VT clock: a key that reads what is left and goes red for the last ten seconds; the rehearsal's skip; the top.
	const vtOn = litAs('video_on_air', { out: false }, 'presenter', 'on')
	const vtOut = litAs('video_on_air', { out: true }, 'presenter', 'out')
	add('video_clock', key('Presenter', 'VT clock — what is left of the clip on air (red for its last ten seconds); press for its last ten seconds', '$(patterns:video_chip)', '18', press('video_end', { seconds: 10 }), [vtOn, vtOut]))
	add('video_end', key('Presenter', "VT — jump to the clip's last ten seconds (rehearsal)", 'VT\n⏭ LAST 10 s', '14', press('video_end', { seconds: 10 }), [vtOn, vtOut]))
	add('video_restart', key('Presenter', 'VT — the clip on air from the top', 'VT\n⟲ TOP', '14', press('video_restart'), [vtOn]))
	const webOn = litAs('web_on_air', { word: '' }, 'presenter', 'on')
	const webArmed = litAs('web_armed', {}, 'presenter', 'on')
	add('web_vt_arm', key('Web page', 'Armed VT — ARM (at the mark, else where the player is): $(patterns:web_armed)', 'ARM\nVT', '14', press('web_vt', { mode: 'arm', time: '', page: '' }), [webArmed]))
	add('web_vt_mark', key('Web page', 'Armed VT — MARK where the player is now', 'MARK\nVT', '14', press('web_vt', { mode: 'mark', time: '', page: '' }), [webArmed]))
	add('web_vt_disarm', key('Web page', 'Armed VT — DISARM', 'DISARM\nVT', '14', press('web_vt', { mode: 'disarm', time: '', page: '' }), [webArmed]))
	for (const [id, text, action] of [
		['next', 'PAGE\nNEXT ▶', 'next'], ['prev', 'PAGE\n◀ PREV', 'prev'], ['first', 'PAGE\nFIRST', 'first'], ['last', 'PAGE\nLAST', 'last'],
		['present', 'PAGE\nPRESENT', 'present'], ['exit', 'PAGE\nEXIT', 'exit'], ['play', 'PAGE\nPLAY ❚❚', 'play'], ['mute', 'PAGE\nMUTE', 'mute'],
		['black', 'PAGE\nBLACK', 'black'], ['reload', 'PAGE\nRELOAD', 'reload'],
	]) {
		add(`web_${id}`, key('Web page', `Web page — ${action} (the page on air: $(patterns:web_page))`, text, '14', press('web_action', { action, key: '', page: '' }), [webOn]))
	}
	const musicOn = litAs('music_playing', {}, 'music', 'playing')
	add('music_play', key('Break music', 'Break music — play / resume', 'BREAK\n▶', '14', press('music', { mode: 'PLAY' }), [musicOn]))
	add('music_pause', key('Break music', 'Break music — pause', 'BREAK\n❚❚', '14', press('music', { mode: 'PAUSE' }), [musicOn]))
	add('music_skip', key('Break music', 'Break music — skip track', 'BREAK\n⏭', '14', press('music', { mode: 'NEXT' }), [musicOn]))
	for (let n = 1; n <= 6; n++) add(`music_${n}`, key('Break music', `Break music ${n} (labels itself)`, `♫ ${n}\n$(patterns:music_${n})`, 'auto', press('music_item', { n }), [musicOn, empty('music', n)]))
	add('next', key('Presenter', 'Next step', 'NEXT\n$(patterns:presenter_step)/$(patterns:presenter_count)', '14', press('presenter_next'), [], { bgcolor: rgb(COLOURS.steel) }))
	add('prev', key('Presenter', 'Previous step', 'BACK', '18', press('presenter_prev')))
	for (let slot = 1; slot <= 12; slot++) {
		add(`look_${slot}`, key('Looks', `Look F${slot} (labels itself)`, `F${slot}\n$(patterns:look_f${slot})`, 'auto', press('look_slot', { slot }), [litAs('look_f_on_air', { slot }, 'look', 'air'), empty('look_f', slot)]))
	}
	for (let n = 1; n <= 8; n++) {
		add(`screen_${n}`, key('Screens', `Screen ${n} toggle (labels itself)`, `$(patterns:screen_${n})`, 'auto', press('screen', { n, mode: 'TOGGLE' }), [
			litAs('screen_enabled', { n }, 'screen', 'enabled'), litAs('screen_locked', { n }, 'screen', 'locked'), empty('screen', n),
		]))
		add(`screen_${n}_lock`, key('Screens', `Screen ${n} lock / unlock`, `LOCK\n$(patterns:screen_${n})`, 'auto', press('screen_lock', { n, mode: 'TOGGLE' }), [litAs('screen_locked', { n }, 'screen', 'locked'), empty('screen', n)]))
		// Amber when this screen has gone its own way inside the look on air, so the key that puts it back is the key that tells you it needs putting back.
		add(`screen_${n}_program`, key('Screens', `Screen ${n} back to the program`, `PGM\n$(patterns:screen_${n})`, 'auto', press('screen_program', { n }), [litAs('screen_off_look', { n }, 'screen', 'offLook'), empty('screen', n)]))
		// What this screen is actually drawing, and whether that is still what the look asked.
		add(`screen_${n}_picture`, key('Screens', `Screen ${n} — the picture it is showing`, `$(patterns:screen_${n})\n$(patterns:screen_${n}_pattern)`, 'auto', press('screen_program', { n }), [
			litAs('screen_own', { n }, 'screen', 'own'), litAs('screen_off_look', { n }, 'screen', 'offLook'), empty('screen', n),
		]))
		// TAKE the desk's preview to this screen alone (round 63): green when the screen shows its own picture — the key says what it did.
		add(`screen_${n}_take`, key('Screens', `Screen ${n} — TAKE the preview to it alone`, `TAKE\n$(patterns:screen_${n})`, 'auto', press('screen_take', { n, mode: 'TAKE' }), [litAs('screen_own', { n }, 'screen', 'own'), empty('screen', n)]))
		add(`screen_${n}_fade_down`, key('Screens', `Screen ${n} fade to black — 2 s`, `FADE ▼\n$(patterns:screen_${n})`, 'auto', fade('DOWN', 2, `SCREEN ${n}`), [litAs('screen_black', { n }, 'screen', 'black'), empty('screen', n)]))
		add(`screen_${n}_fade_up`, key('Screens', `Screen ${n} fade up — 2 s`, `FADE ▲\n$(patterns:screen_${n})`, 'auto', fade('UP', 2, `SCREEN ${n}`), [empty('screen', n)]))
		// Round 67 — the screen's group: MAIN follows the programme; CONF is a stage monitor that locks as it takes the group. The key reads the group and lights when the screen is in it.
		add(`screen_${n}_group_main`, key('Screens', `Screen ${n} — group MAIN (the audience's picture)`, `MAIN\n$(patterns:screen_${n})`, 'auto', press('screen_group', { n, group: 'main' }), [litAs('screen_group_is', { n, group: 'main' }, 'screen', 'group'), empty('screen', n)]))
		add(`screen_${n}_group_conf`, key('Screens', `Screen ${n} — group CONFIDENCE (a stage monitor; locks)`, `CONF\n$(patterns:screen_${n})`, 'auto', press('screen_group', { n, group: 'confidence' }), [litAs('screen_group_is', { n, group: 'confidence' }, 'screen', 'group'), empty('screen', n)]))
	}
	for (const letter of ['A', 'B', 'C', 'D']) {
		add(`group_${letter}_on`, key('Screens', `Canvas ${letter} on`, `${letter}\nON`, '14', press('group', { letter, mode: 'ON' })))
		add(`group_${letter}_off`, key('Screens', `Canvas ${letter} off`, `${letter}\nOFF`, '14', press('group', { letter, mode: 'OFF' })))
		add(`group_${letter}_fade_down`, key('Screens', `Canvas ${letter} fade to black — 2 s`, `${letter}\nFADE ▼`, '14', fade('DOWN', 2, `GROUP ${letter}`)))
		add(`group_${letter}_fade_up`, key('Screens', `Canvas ${letter} fade up — 2 s`, `${letter}\nFADE ▲`, '14', fade('UP', 2, `GROUP ${letter}`)))
	}
	for (let n = 1; n <= 8; n++) add(`stinger_${n}`, key('Stingers', `Stinger ${n} (labels itself)`, `$(patterns:stinger_${n})`, 'auto', press('stinger', { n }), [litAs('stinger_playing', {}, 'stinger', 'playing'), empty('stinger', n)]))
	add('stinger_stop', key('Stingers', 'Stop stinger', 'STING\nSTOP', '14', press('stinger_stop'), [], { bgcolor: rgb(COLOURS.off) }))
	for (let n = 1; n <= 8; n++) {
		add(`vog_${n}`, key('VOG', `VOG ${n} (labels itself)`, `VOG\n$(patterns:stinger_${n})`, 'auto', press('vog', { n }), [litAs('vog_playing', {}, 'vog', 'playing'), empty('stinger', n)]))
		add(`sting_${n}`, key('Stingers', `Stinger ${n} (kind-checked, labels itself)`, `STING\n$(patterns:stinger_${n})`, 'auto', press('sting', { n }), [litAs('sting_playing', {}, 'stinger', 'playing'), litAs('sting_hold', {}, 'stinger', 'hold')]))
	}
	add('sting_hold_release', key('Stingers', 'Held stinger — put it back', 'HOLD\nBACK', '14', press('stinger_stop'), [litAs('sting_hold', {}, 'stinger', 'hold')]))
	const audioOn = litAs('audio_playing', {}, 'audio', 'playing')
	add('audio_play', key('Audio', 'Audio play', '♪ PLAY', '14', press('audio', { mode: 'PLAY' }), [audioOn]))
	add('audio_stop', key('Audio', 'Audio stop', '♪ STOP', '14', press('audio', { mode: 'STOP' })))
	add('audio_routing', key('Audio', 'Audio routing — the matrix (which soundtrack goes where) on or off', 'ROUTING\n$(patterns:audio_routing)', '14', press('audio_routing', { mode: 'toggle' }), [litAs('audio_routing_on', {}, 'audio', 'playing')]))
	add('audio_next', key('Audio', 'Audio playlist — next track', '♪ ⏭\n$(patterns:audio_next)', 'auto', press('audio', { mode: 'NEXT' }), [audioOn]))
	add('audio_prev', key('Audio', 'Audio playlist — previous track', '♪ ⏮', '14', press('audio', { mode: 'PREV' }), [audioOn]))
	add('audio_now', key('Audio', 'Audio playlist — what is on (press: play / resume)', '♪ $(patterns:audio_n)/$(patterns:audio_count)\n$(patterns:audio_track)\n$(patterns:audio_remaining)', 'auto', press('audio', { mode: 'PLAY' }), [audioOn]))
	for (let n = 1; n <= 8; n++) add(`track_bank_${n}`, key('Audio', `Audio playlist track ${n} (labels itself)`, `♪ ${n}\n$(patterns:track_${n})`, 'auto', press('audio_item', { n }), [audioOn, empty('track', n)]))
	// The install: the schedule's switch (a latch: press on, press off), an announcement by name, an advert by number, the END keys.
	out['install_schedule'] = { category: 'Install', preset: {
		type: 'simple', name: 'SCHEDULE — the clock runs the site (green while on)', style: { text: 'SCHEDULE\n$(patterns:install)', size: '14', ...idle() },
		steps: [{ down: press('schedule', { mode: 'ON' }), up: [] }, { down: press('schedule', { mode: 'OFF' }), up: [] }],
		feedbacks: [litAs('schedule_on', {}, 'install', 'schedule')],
	} }
	add('install_announce', key('Install', 'ANNOUNCE — an announcement by name (edit the name)', 'ANNOUNCE\nClosing time', '14', press('announce', { what: 'Closing time' }), [litAs('announcement_on', { name: 'Closing time' }, 'install', 'announcement')]))
	add('install_announce_off', key('Install', 'ANNOUNCE OFF — the announcement on ends', 'ANNOUNCE\nOFF', '14', press('announce_off'), [litAs('announcement_on', { name: '' }, 'install', 'announcement')]))
	for (let n = 1; n <= 4; n++) add(`install_advert_${n}`, key('Install', `ADVERT ${n} — the advert at place ${n} of the Install page, now`, `ADVERT\n${n}`, '14', press('advert', { name: String(n) }), [litAs('advert_on', { name: '' }, 'install', 'advert')]))
	add('install_advert_off', key('Install', 'ADVERT OFF — the advert on ends, the programme comes back', 'ADVERT\nOFF', '14', press('advert_off'), [litAs('advert_on', { name: '' }, 'install', 'advert')]))
	add('install_status', key('Install', 'The install: the programme on and the next change', '$(patterns:install_programme)\n$(patterns:install_next)', 'auto', []))

	// ---- the rig (round 65.10): the signal verdicts, the test route, the known-good rig, the flow ------------
	for (let n = 1; n <= 8; n++) {
		add(`rig_signal_${n}`, key('Rig', `Screen ${n} — signal result: green MATCH, red MISMATCH (press: TEST ROUTE toggles)`, `S${n} SIGNAL\n$(patterns:screen_${n}_signal)`, 'auto', press('screen_testroute', { n, mode: 'TOGGLE' }),
			[litAs('screen_signal_is', { n, result: 'MATCH' }, 'signal', 'match'), litAs('screen_signal_is', { n, result: 'MISMATCH' }, 'signal', 'mismatch'), empty('screen', n)]))
	}
	add('rig_known_good', key('Rig', 'KNOWN GOOD — green while the rig is the one saved, amber when it moved; press: RIG SAVE first show', 'KNOWN\nGOOD\n$(patterns:machine_rig)', 'auto', press('rig_save', { note: 'first show' }),
		[litAs('rig_known_good', {}, 'rig', 'same'), litAs('rig_drift', {}, 'rig', 'drift')]))
	add('rig_commissioning', key('Rig', 'COMMISSIONING — the flow\'s headline and the next step; green once every stage is', '$(patterns:commissioning)\n$(patterns:commissioning_next)', 'auto', [],
		[litAs('commissioned', {}, 'rig', 'commissioned')]))
	add('rig_inventory', key('Rig', 'THIS MACHINE — the card, its driver, the displays, the audio, the power plan', '$(patterns:machine_inventory)', 'auto', []))
	// Round 66 — the God's Eye page: the headline lit by the worst light, the problems stepped, the lenses, the whole picture.
	add('eye_headline', key('Eye', "GOD'S EYE — the picture's headline, lit by the worst light (red wrong now, amber needs a look, green all green); press: NEXT problem", '$(patterns:eye_headline)', 'auto', press('eye_next'),
		[litAs('eye_worst', { light: 'red' }, 'eye', 'red'), litAs('eye_worst', { light: 'amber' }, 'eye', 'amber'), litAs('eye_worst', { light: 'green' }, 'eye', 'green')]))
	add('eye_next', key('Eye', 'NEXT PROBLEM — the eye moves to the next red or amber thing (amber while there are any)', 'NEXT\nPROBLEM\n$(patterns:eye_problems)', 'auto', press('eye_next'), [litAs('eye_problems', {}, 'eye', 'amber')]))
	add('eye_prev', key('Eye', 'PREVIOUS PROBLEM', 'PREV\nPROBLEM', 'auto', press('eye_prev')))
	add('eye_reset', key('Eye', 'RESET — the whole picture, nothing dimmed, the view from before the focus', 'EYE\nRESET', 'auto', press('eye_reset')))
	for (const lens of ['all', 'video', 'control', 'audio', 'room', 'problems']) add(`eye_lens_${lens}`, key('Eye', `LENS ${lens.toUpperCase()} — the picture through one lens`, `LENS\n${lens.toUpperCase()}`, 'auto', press('eye_lens', { lens })))
	add('eye_worst', key('Eye', 'WORST — the worst thing by name and what is wrong; press: the eye on screen 1', '$(patterns:eye_worst)', 'auto', press('eye_focus', { words: 'screen 1' })))

	// ---- the stage: the speaker's timer as the speaker sees it, and the messages ----------------------------
	const stageColours = [litAs('stage_colour_is', { colour: 'green' }, 'stage', 'running'), litAs('stage_colour_is', { colour: 'amber' }, 'stage', 'amber'), litAs('stage_colour_is', { colour: 'red' }, 'stage', 'red'), litAs('stage_is', { phase: 'paused' }, 'stage', 'paused')]
	const timerSimple = key('Stage', 'STAGE TIMER — what the speaker sees, in the timer\'s own colour; press: pause / resume', '$(patterns:stage_timer)\n$(patterns:stage_label)', 'auto', press('timer', { mode: 'TOGGLE', seconds: 60 }), stageColours).preset
	// The same key drawn with Companion 5's graphics: a ring for how far through the segment is, the digits in the
	// middle, the ground in the timer's own colour — offered first, with the plain key for a Companion that has no layers.
	const timerLayered = {
		type: 'layered', name: timerSimple.name,
		canvas: { decoration: 'none' },
		elements: [
			{ id: 'ground', type: 'box', x: 0, y: 0, width: 100, height: 100, color: dark },
			{ id: 'ring', type: 'gauge', x: 6, y: 6, width: 88, height: 88, orientation: 'ring', min: 0, max: 100, value: { isExpression: true, value: '$(patterns:stage_progress)' }, ringWidth: 9, roundedEnds: true, trackStyle: 'dimmed', stops: [{ value: 0, color: white, gradient: false }] },
			{ id: 'digits', type: 'text', x: 10, y: 28, width: 80, height: 30, text: { isExpression: true, value: '$(patterns:stage_timer)' }, fontsize: 22, fontsizeAllowShrink: true, color: white, halign: 'center', valign: 'center' },
			{ id: 'label', type: 'text', x: 10, y: 58, width: 80, height: 18, text: { isExpression: true, value: '$(patterns:stage_label)' }, fontsize: 10, fontsizeAllowShrink: true, color: white, halign: 'center', valign: 'center' },
		],
		steps: [{ down: press('timer', { mode: 'TOGGLE', seconds: 60 }), up: [] }],
		feedbacks: [
			{ feedbackId: 'stage_colour_is', options: { colour: 'green' }, styleOverrides: [{ elementId: 'ground', elementProperty: 'color', override: rgb(COLOURS.green) }] },
			{ feedbackId: 'stage_colour_is', options: { colour: 'amber' }, styleOverrides: [{ elementId: 'ground', elementProperty: 'color', override: rgb(COLOURS.amber) }, { elementId: 'digits', elementProperty: 'color', override: ink }, { elementId: 'label', elementProperty: 'color', override: ink }, { elementId: 'ring', elementProperty: 'stops', override: [{ value: 0, color: ink, gradient: false }] }] },
			{ feedbackId: 'stage_colour_is', options: { colour: 'red' }, styleOverrides: [{ elementId: 'ground', elementProperty: 'color', override: rgb(COLOURS.red) }] },
			{ feedbackId: 'stage_is', options: { phase: 'paused' }, styleOverrides: [{ elementId: 'ground', elementProperty: 'color', override: rgb(COLOURS.sky) }, { elementId: 'digits', elementProperty: 'color', override: ink }, { elementId: 'label', elementProperty: 'color', override: ink }] },
		],
	}
	out['stage_timer'] = { category: 'Stage', preset: { type: 'alternatives', variants: [timerLayered, timerSimple] } }
	add('stage_pause', key('Stage', 'STAGE TIMER — pause, or resume when paused', '⏸ / ▶\nTIMER', '14', press('timer', { mode: 'TOGGLE', seconds: 60 }), [litAs('stage_is', { phase: 'paused' }, 'stage', 'paused')]))
	add('stage_add', key('Stage', 'STAGE TIMER +1 MIN', 'TIMER\n+1 MIN', '14', press('timer', { mode: 'ADD', seconds: 60 })))
	add('stage_minus', key('Stage', 'STAGE TIMER −1 MIN', 'TIMER\n−1 MIN', '14', press('timer', { mode: 'MINUS', seconds: 60 })))
	add('stage_flash', key('Stage', 'FLASH — the stage pages blink, the speaker\'s eye to the clock', 'FLASH\nSTAGE', '14', press('timer', { mode: 'FLASH', seconds: 60 })))
	add('stage_wrap', key('Stage', 'WRAP UP — a message to the speaker (edit the words); amber until the stage page ACKs it', 'SPEAKER\nWRAP UP', '14', press('stage_message', { channel: 'speaker', text: 'Wrap up' }), [litAs('stage_pending', { channel: 'speaker' }, 'stage', 'pending')]))
	add('stage_crew', key('Stage', 'A message to the crew (edit the words); amber until the crew page ACKs it', 'CREW\nSTAND BY', '14', press('stage_message', { channel: 'crew', text: 'Stand by' }), [litAs('stage_pending', { channel: 'crew' }, 'stage', 'pending')]))
	add('stage_pending', key('Stage', 'What the stage is waiting to see (press: every message marked seen)', 'STAGE\n$(patterns:stage_pending)', 'auto', press('stage_clear'), [litAs('stage_pending', { channel: 'any' }, 'stage', 'pending')]))
	add('stage_segment', key('Stage', 'The running order — the cue running and the next', '$(patterns:stage_segment)\n▸ $(patterns:stage_next)', 'auto', []))

	// ---- the nodes and the twin: every other Patterns on the network, on keys that label themselves -----------
	for (let n = 1; n <= 8; n++) {
		add(`node_${n}`, key('Nodes', `Node ${n} — the node at place ${n} on the Nodes page, in its kind's colour, dark when gone (labels itself)`, `$(patterns:node_${n})`, 'auto', [], [
			litAs('node_is', { n, kind: 'desk' }, 'node', 'desk'), litAs('node_is', { n, kind: 'caller' }, 'node', 'caller'),
			litAs('node_is', { n, kind: 'arcade' }, 'node', 'arcade'), litAs('node_is', { n, kind: 'timer' }, 'node', 'timer'),
			litAs('node_gone', { n }, 'node', 'gone'), empty('node', n),
		]))
	}
	add('nodes_count', key('Nodes', 'How many nodes the desk hears, and which', 'NODES\n$(patterns:nodes_count)', '14', [], [litAs('node_linked', { min: 1 }, 'node', 'caller')]))
	add('twin_state', key('Twin', 'The twin — its role and state, green in step, red when the main is silent, amber after a takeover', 'TWIN\n$(patterns:twin_role)\n$(patterns:twin_phase)', 'auto', [], [
		litAs('twin_is', { phase: 'inStep' }, 'twin', 'inStep'), litAs('twin_is', { phase: 'mainSilent' }, 'twin', 'silent'), litAs('twin_is', { phase: 'tookOver' }, 'twin', 'tookOver'),
	]))
	// TAKE OVER is two presses on purpose: the first arms the key (step 2 reads SURE?), the second sends; a wrong first press is undone by pressing STAND BY.
	out['twin_take_over'] = { category: 'Twin', preset: {
		type: 'simple', name: 'TAKE OVER — two presses: the first arms the key, the second sends TWIN TAKEOVER (on a standby)', style: { text: 'TAKE\nOVER', size: '14', ...idle() },
		options: { stepAutoProgress: true },
		steps: [{ down: [], up: [] }, { name: 'SURE?', down: press('twin', { mode: 'TAKEOVER' }), up: [] }],
		feedbacks: [
			{ feedbackId: 'internal:buttonCurrentStep', options: { step: 2 }, style: { ...on('amber'), text: 'SURE?\nTAKE OVER' } },
			litAs('twin_is', { phase: 'mainSilent' }, 'twin', 'silent'), litAs('twin_is', { phase: 'tookOver' }, 'twin', 'tookOver'),
		],
	} }
	add('twin_standby', key('Twin', 'STAND BY again — a twin that took over follows the main (also disarms TAKE OVER)', 'STAND\nBY', '14', press('twin', { mode: 'STANDBY' }), [litAs('twin_is', { phase: 'inStep' }, 'twin', 'inStep')]))
	out['twin_take_back'] = { category: 'Twin', preset: {
		type: 'simple', name: 'TAKE BACK — two presses: the first arms the key, the second sends TWIN TAKEBACK (on the main)', style: { text: 'TAKE\nBACK', size: '14', ...idle() },
		options: { stepAutoProgress: true },
		steps: [{ down: [], up: [] }, { name: 'SURE?', down: press('twin', { mode: 'TAKEBACK' }), up: [] }],
		feedbacks: [{ feedbackId: 'internal:buttonCurrentStep', options: { step: 2 }, style: { ...on('amber'), text: 'SURE?\nTAKE BACK' } }],
	} }
	add('showlock', key('Transport', 'SHOW LOCK — the machine held for the show (notifications, sounds, sleep, the Windows key)', 'SHOW\nLOCK', '14', press('showlock', { mode: 'ON' })))

	const presets = {}
	const categories = {}
	for (const [id, { category, preset }] of Object.entries(out)) {
		presets[id] = preset
		categories[id] = category
	}
	return { presets, categories }
}

/** Presets built from the show's own lists — one key per item, named for it — under "… — this show" categories, rebuilt when the lists change. */
export function buildShowPresets(s) {
	const out = {}
	const add = (id, built) => { out[id] = built }
	const safe = (text) => String(text ?? '').replace(/[^A-Za-z0-9]+/g, '_').slice(0, 40)
	;(s.looks ?? []).forEach((l, i) => {
		add(`show_look_${i + 1}_${safe(l.name)}`, key('Looks — this show', `Look: ${l.name}${l.slot ? ` (F${l.slot})` : ''}`, l.name, 'auto', press('look_name', { name: l.name }), [
			litAs('look_on_air', { name: l.name }, 'look', 'air'), litAs('look_edited', { name: l.name }, 'look', 'edited'), litAs('look_preview', { name: l.name }, 'look', 'preview'),
		]))
	})
	;(s.lowerThirds ?? []).forEach((d) => add(`show_lt_${d.n}_${safe(d.name)}`, key('Lower thirds — this show', `Lower third: ${d.name}`, `LT\n${d.name}`, 'auto', press('lower_third', { n: d.n }), [litAs('lower_third_on', { name: d.name }, 'lowerThird', 'on')])))
	;(s.people ?? []).forEach((p) => add(`show_person_${p.n}_${safe(p.name)}`, key('People — this show', `Person: ${p.name}${p.role ? ` — ${p.role}` : ''}`, p.name, 'auto', press('lower_third_person', { n: p.n, design: '' }), [litAs('lower_third_person_is', { name: p.name }, 'lowerThird', 'person')])))
	;(s.stingers ?? []).forEach((it) => {
		const vog = it.kind === 'vog'
		add(`show_stinger_${it.n}_${safe(it.name)}`, key(vog ? 'VOGs — this show' : 'Stingers — this show', `${vog ? 'VOG' : 'Stinger'}: ${it.name}`, `${vog ? 'VOG' : 'STING'}\n${it.name}`, 'auto', press(vog ? 'vog' : 'sting', { n: it.n }),
			vog ? [litAs('vog_playing', {}, 'vog', 'playing')] : [litAs('sting_playing', {}, 'stinger', 'playing'), litAs('sting_hold', {}, 'stinger', 'hold')]))
	})
	;(s.audio?.items ?? []).forEach((t) => add(`show_track_${t.n}_${safe(t.name)}`, key('Audio playlist — this show', `Audio track: ${t.name}`, `♪\n${t.name}`, 'auto', press('audio_item', { n: t.n }), [litAs('audio_playing', {}, 'audio', 'playing')])))
	;(s.music?.items ?? []).forEach((m) => add(`show_music_${m.n}_${safe(m.name)}`, key('Break music — this show', `Break music: ${m.name}`, `♫\n${m.name}`, 'auto', press('music_item', { n: m.n }), [litAs('music_playing', {}, 'music', 'playing')])))
	;(s.sections ?? []).forEach((x) => add(`show_section_${x.n}_${safe(x.name)}`, key('Playlist parts — this show', `Playlist part: ${x.name}`, x.name, 'auto', press('section', { n: x.n }))))
	;(s.screens ?? []).forEach((sc) => {
		add(`show_screen_${sc.n}_${safe(sc.label)}`, key('Screens — this show', `Screen: ${sc.label}`, sc.label, 'auto', press('screen', { n: sc.n, mode: 'TOGGLE' }), [
			litAs('screen_enabled', { n: sc.n }, 'screen', 'enabled'), litAs('screen_locked', { n: sc.n }, 'screen', 'locked'),
		]))
	})
	upcoming(s).forEach((c, i) => {
		add(`show_cue_${i + 1}_${safe(c.number)}`, key('Upcoming cues — this show', `Cue ${c.number} ${c.name} — standby`, `${c.number}\n${c.name}`, 'auto', press('cue_standby', { mode: 'NUMBER', cue: c.number }), [litAs('cue_standby_is', { cue: c.number }, 'cue', 'standby')]))
	})
	;(s.patternKinds ?? []).forEach((kind) => add(`pattern_${safe(kind)}`, key('Patterns — every kind', `Pattern: ${kind}`, String(kind).replace(/([a-z])([A-Z])/g, '$1\n$2'), 'auto', press('pattern', { kind }), [litAs('pattern_is', { kind }, 'screen', 'pattern')])))
	const presets = {}
	const categories = {}
	for (const [id, { category, preset }] of Object.entries(out)) {
		presets[id] = preset
		categories[id] = category
	}
	return { presets, categories }
}
