// The feedbacks: a key's colour from the desk's STATE. Every default style comes from the palette,
// so a look on air is the same green on every key that says so, and a thing in its preview state
// the same amber. ctx: { state() }
import { norm, bankName } from './state.js'
import { style, empty } from './palette.js'

const screenN = { type: 'number', id: 'n', label: 'Screen number', default: 1, min: 1, max: 32 }
const named = (id, label, def = '') => ({ type: 'textinput', id, label, default: def })

export function buildFeedbacks(ctx) {
	const s = () => ctx.state()
	const bool = (name, defaultStyle, options, callback) => ({ type: 'boolean', name, defaultStyle, options, callback })
	return {
		blackout: bool('Blackout is on', style('transport', 'blackout'), [], () => s().blackout === true),
		screen_enabled: bool('Screen is enabled', style('screen', 'enabled'), [screenN], (fb) => s().screens?.some((x) => x.n === fb.options.n && x.enabled) === true),
		screen_locked: bool('Screen is locked (keeps its picture through looks, cues and TAKE)', style('screen', 'locked'), [screenN], (fb) => s().screens?.some((x) => x.n === fb.options.n && x.locked) === true),
		screen_armed: bool('Screen is armed — the next CUT / TAKE changes it', style('screen', 'armed'), [screenN], (fb) => s().screens?.some((x) => x.n === fb.options.n && x.armed) === true),
		screen_own: bool("Screen shows a picture of its own, not the program's", style('screen', 'own'), [screenN], (fb) => s().screens?.some((x) => x.n === fb.options.n && x.own) === true),
		// Faded to black on its own (FADE … SCREEN n / GROUP A / FOCUSED / TICKED) — the blackout is a separate feedback.
		screen_black: bool('Screen is faded to black on its own', style('screen', 'black'), [screenN], (fb) => s().screens?.some((x) => x.n === fb.options.n && x.black) === true),
		black_any: bool('Any screen is faded to black on its own', style('transport', 'black'), [], () => (s().black?.count ?? 0) > 0),
		// Looks: which one is on air (by name, by its place in the list, or by its F-key) and which is in the preview.
		look_on_air: bool('A look is on air (a named one, or any)', style('look', 'air'), [named('name', 'Look name (blank = any look)')], (fb) => {
			const on = s().airLook ?? ''
			return on !== '' && (!fb.options.name || on === fb.options.name)
		}),
		// THE THREE STATES A LOOK KEY NEEDS: up and untouched, up but somebody has been at the picture since, not up.
		// Stack look_on_air (green) under look_edited (amber) on the same key — the later matching feedback wins.
		look_edited: bool('A look is on air BUT the picture has changed since it was recalled', style('look', 'edited'), [named('name', 'Look name (blank = whichever look is on air)')], (fb) => {
			const on = s().airLook ?? ''
			if (on === '' || !s().lookEdited) return false
			return !fb.options.name || on === fb.options.name
		}),
		look_screens_off: bool('A look is on air but one or more screens have changed pattern within it', style('look', 'screensOff'), [], () => (s().airLook ?? '') !== '' && (s().lookScreensOff ?? 0) > 0),
		screen_off_look: bool('Screen has gone its own way — not what the look on air asked of it', style('screen', 'offLook'), [screenN], (fb) => s().screens?.some((x) => x.n === fb.options.n && x.off) === true),
		screen_pattern_is: bool('Screen is showing a kind of picture', style('screen', 'pattern'), [screenN, named('kind', 'Pattern kind (Grid, ColorBars, TestCard, Media…)', 'TestCard')], (fb) => {
			const want = norm(fb.options.kind)
			const screen = s().screens?.find((x) => x.n === fb.options.n)
			return !!screen && want !== '' && norm(screen.pattern) === want
		}),
		look_bank_on_air: bool("The look at a place in the show's list is on air (bank key n)", style('look', 'air'), [{ type: 'number', id: 'n', label: 'Place in the list (1–16)', default: 1, min: 1, max: 16 }], (fb) => !!s().looks?.[fb.options.n - 1]?.air),
		look_bank_edited: bool('The look at a place in the list is on air but has been changed since (bank key n)', style('look', 'edited'), [{ type: 'number', id: 'n', label: 'Place in the list (1–16)', default: 1, min: 1, max: 16 }], (fb) => !!s().looks?.[fb.options.n - 1]?.air && s().lookEdited === true),
		look_f_on_air: bool('The look on an F-key is on air', style('look', 'air'), [{ type: 'number', id: 'slot', label: 'F-key (1–12)', default: 1, min: 1, max: 12 }], (fb) => !!s().looks?.find((l) => l.slot === fb.options.slot)?.air),
		// The install: the clock running, an announcement or an advert over the programme.
		schedule_on: bool('The install schedule is on (the clock runs the site)', style('install', 'schedule'), [], () => s().install?.on === true),
		announcement_on: bool('An announcement is on (any, or a named one)', style('install', 'announcement'), [named('name', 'Announcement name (blank = any)')], (fb) => {
			const i = s().install ?? {}
			return i.overKind === 'announcement' && (!fb.options.name || i.over === fb.options.name)
		}),
		advert_on: bool('An advert is on (any, or a named one)', style('install', 'advert'), [named('name', 'Advert name (blank = any)')], (fb) => {
			const i = s().install ?? {}
			return i.overKind === 'advert' && (!fb.options.name || i.over === fb.options.name)
		}),
		look_preview: bool('A look is loaded in the preview (a named one, or any)', style('look', 'preview'), [named('name', 'Look name (blank = any look)')], (fb) => {
			const on = s().previewLook ?? ''
			return on !== '' && (!fb.options.name || on === fb.options.name)
		}),
		// A bank key with nothing behind it dims, so a page of sixteen look keys shows only the looks the show has.
		slot_empty: bool('Bank key has nothing behind it (dim the key)', empty(), [
			{ type: 'dropdown', id: 'kind', label: 'Bank', default: 'look', choices: [
				{ id: 'look', label: 'Looks by place' }, { id: 'look_f', label: 'Looks by F-key' }, { id: 'lt', label: 'Lower thirds' }, { id: 'person', label: 'People' },
				{ id: 'stinger', label: 'VOGs and stingers' }, { id: 'music', label: 'Break music' }, { id: 'track', label: 'Audio playlist' }, { id: 'section', label: 'Playlist parts' },
				{ id: 'screen', label: 'Screens' }, { id: 'cue', label: 'Upcoming cues' }, { id: 'node', label: 'Nodes' },
			] },
			{ type: 'number', id: 'n', label: 'Place', default: 1, min: 1, max: 32 },
		], (fb) => bankName(s(), fb.options.kind, fb.options.n) === ''),
		audio_playing: bool('Audio track is playing', style('audio', 'playing'), [], () => s().audio?.playing === true),
		stinger_playing: bool('A stinger is on air', style('stinger', 'playing'), [], () => (s().stingerPlaying ?? '') !== ''),
		vog_playing: bool('A VOG is on air', style('vog', 'playing'), [], () => s().stingerKind === 'vog' || (s().vogSound ?? '') !== ''),
		sting_playing: bool('A stinger is on air', style('stinger', 'playing'), [], () => s().stingerKind === 'sting'),
		sting_hold: bool('A stinger is holding the screens', style('stinger', 'hold'), [], () => (s().stingHold ?? '') !== ''),
		duck_on: bool('The live duck is on', style('duck', 'on'), [], () => !!s().duck),
		lower_third_on: bool('A lower third is on screen (any, or a named one)', style('lowerThird', 'on'), [named('name', 'Design name (blank = any)')], (fb) => {
			const on = s().lowerThird ?? ''
			return on !== '' && (!fb.options.name || on === fb.options.name)
		}),
		review_on: bool('Review is on (the preview fills every multiview)', style('transport', 'review'), [], () => !!s().review),
		weather_on: bool('The weather chip is on air', style('weather', 'on'), [], () => !!s().weather?.on),
		frozen: bool('Frozen (every output holds its frame)', style('transport', 'frozen'), [], () => !!s().frozen),
		lower_third_person_is: bool('A given person is on screen (the name the lower third on air carries)', style('lowerThird', 'person'), [named('name', 'Person name')], (fb) => {
			const on = s().lowerThirdPerson ?? ''
			return on !== '' && on === fb.options.name
		}),
		lower_third_preview: bool('A lower third is in the preview for a sign-off (any, or a named one)', style('lowerThird', 'preview'), [named('name', 'Design name (blank = any)')], (fb) => {
			const on = s().lowerThirdPreview ?? ''
			return on !== '' && (!fb.options.name || on === fb.options.name)
		}),
		lower_third_edited: bool('The lower third on air was edited after it went there (UPDATE pushes the edit)', style('lowerThird', 'edited'), [], () => !!s().lowerThirdEdited),
		web_on_air: bool('A web page is on air (any, or one whose address carries a word)', style('presenter', 'on'), [named('word', 'A word of the address (blank = any page)')], (fb) => {
			const w = s().web
			if (!w || !w.page) return false
			const word = String(fb.options.word || '').trim().toLowerCase()
			return !word || String(w.url || '').toLowerCase().includes(word) || String(w.page || '').toLowerCase().includes(word)
		}),
		audio_routing_on: bool('The routing matrix is in charge of which soundtrack goes where', style('audio', 'playing'), [], () => !!(s().audioRouting && s().audioRouting.on)),
		web_armed: bool("A web page's video is armed — held at its mark, to play when the page goes to air", style('presenter', 'on'), [], () => !!s().webArmed),
		web_advert: bool('An advert is showing over the web page on air (Patterns skips it when the site allows)', style('presenter', 'out'), [], () => !!(s().web && s().web.player && s().web.player.ad)),
		deck_on_air: bool('A deck (PDF) is on air — or on its last page', style('presenter', 'on'), [{ type: 'checkbox', id: 'ended', label: 'Only on its last page (the next click GOes the standby cue)', default: false }], (fb) => {
			const d = s().deck
			if (!d || !d.count) return false
			return fb.options.ended ? !!d.ended : true
		}),
		video_on_air: bool('A clip is on air — or in its last ten seconds', style('presenter', 'on'), [{ type: 'checkbox', id: 'out', label: 'Only in its last ten seconds (the caller\'s "ten seconds on VT")', default: false }], (fb) => {
			const v = s().video
			if (!v) return false
			return fb.options.out ? v.out === true : true
		}),
		music_playing: bool('Break music is playing', style('music', 'playing'), [], () => s().music?.playing === true),
		// The overlays: the clock (on, its hours, seconds, date), the message (on, scrolling), the countdown (running / over), the logo, the PiP, the kind of picture.
		clock_on: bool('The clock overlay is on air', style('overlay', 'on'), [], () => !!s().overlays?.clock?.on),
		clock_hours: bool('The clock reads 12-hour or 24-hour', style('overlay', 'on'), [{ type: 'dropdown', id: 'hours', label: 'Hours', default: 24, choices: [{ id: 12, label: '12-hour' }, { id: 24, label: '24-hour' }] }], (fb) => (s().overlays?.clock?.hours ?? 24) === Number(fb.options.hours)),
		clock_seconds: bool('The clock shows seconds', style('overlay', 'on'), [], () => !!s().overlays?.clock?.seconds),
		clock_date: bool('The clock shows the date', style('overlay', 'on'), [], () => !!s().overlays?.clock?.date),
		message_on: bool('The message overlay is on air', style('overlay', 'on'), [], () => !!s().overlays?.message?.on),
		message_scroll: bool('The message scrolls as a ticker', style('overlay', 'on'), [], () => !!s().overlays?.message?.scroll),
		countdown_running: bool('The countdown is running — or over', style('countdown', 'running'), [{ type: 'dropdown', id: 'phase', label: 'Phase', default: 'running', choices: [{ id: 'running', label: 'Running' }, { id: 'over', label: 'Over (reached zero)' }, { id: 'any', label: 'Running or over' }] }], (fb) => {
			const p = s().overlays?.countdown?.phase ?? 'off'
			return fb.options.phase === 'any' ? p !== 'off' : p === fb.options.phase
		}),
		logo_on: bool('The logo overlay is on air', style('overlay', 'on'), [], () => !!s().overlays?.logo?.on),
		pip_on: bool('The PiP inset is on air', style('overlay', 'on'), [], () => !!s().overlays?.pip?.on),
		pattern_is: bool('The kind of picture on air is…', style('screen', 'pattern'), [named('kind', 'Kind (Grid, ColorBars, LedWall…)', 'Grid')], (fb) => norm(s().pattern) !== '' && norm(s().pattern) === norm(fb.options.kind)),
		// The stream's whole health block, and the outputs, EDIT SAFE, the tone, the day's lateness, a device open.
		stream_active: bool('The stream is live', style('stream', 'active'), [], () => s().stream?.active === true),
		stream_trouble: bool('The stream is in trouble (dropping frames, reconnecting, slow)', style('stream', 'trouble'), [], () => !!s().stream?.trouble),
		outputs_live: bool('The outputs are open (the audience can see something)', style('screen', 'armed'), [], () => s().live === true),
		edit_safe: bool('EDIT SAFE is open — there is a preview and a TAKE to come', style('transport', 'editSafe'), [], () => s().editSafe === true),
		tone_on: bool('The soundcheck tone is on', style('tone', 'on'), [], () => s().tone === true),
		running_late: bool('The show is running late by more than this many minutes', style('cue', 'late'), [{ type: 'number', id: 'minutes', label: 'Minutes late', default: 5, min: 1, max: 120 }], (fb) => (s().cuestack?.timing?.offsetSeconds ?? 0) >= fb.options.minutes * 60),
		device_open: bool('An Interactive device is open (any, or a named one)', style('device', 'open'), [named('name', 'Device name (blank = any)')], (fb) => {
			const rows = s().devices ?? []
			if (!fb.options.name) return rows.some((d) => d.open)
			return rows.some((d) => d.open && d.name === fb.options.name)
		}),
		device_failing: bool("An Interactive device's last word was a failure — a no, a silence, a port that would not open (any, or a named one)", style('device', 'fault'), [named('name', 'Device name (blank = any)')], (fb) => {
			const rows = s().devices ?? []
			if (!fb.options.name) return rows.some((d) => d.failing)
			return rows.some((d) => d.failing && d.name === fb.options.name)
		}),
		cue_armed: bool('Cue stack is armed', style('cue', 'armed'), [], () => s().cuestack?.armed === true),
		cue_hold: bool('Cue stack is on HOLD', style('cue', 'hold'), [], () => s().cuestack?.hold === true),
		cue_standby_is: bool('A given cue is on standby', style('cue', 'standby'), [named('cue', 'Cue number', '01.010')], (fb) => (s().cuestack?.standby?.number ?? '') === fb.options.cue),
		cue_confirm_required: bool('GO is waiting for confirmation', style('cue', 'confirm'), [], () => !!s().cuestack?.confirm),
		cue_last_failed: bool('The last cue failed or was refused', style('cue', 'failed'), [], () => /Failed|Refused/.test(s().cuestack?.last?.outcome ?? '')),
		render_faulting: bool('An output is faulting — its frames throw and the room sees the last good picture', style('screen', 'fault'), [], () => s().machine?.faulting === true),
		// ---- the nodes, the twin, the stage --------------------------------------------------------------------
		node_is: bool('The node at a place on the Nodes page is of a kind and heard now (bank key n)', style('node', 'desk'), [
			{ type: 'number', id: 'n', label: 'Place (1–8)', default: 1, min: 1, max: 8 },
			{ type: 'dropdown', id: 'kind', label: 'Kind', default: 'any', choices: [{ id: 'any', label: 'Any kind' }, { id: 'desk', label: 'Desk' }, { id: 'caller', label: 'Caller' }, { id: 'arcade', label: 'Arcade' }, { id: 'timer', label: 'Stage timer' }] },
		], (fb) => {
			const node = s().nodes?.[fb.options.n - 1]
			return !!node && node.fresh !== false && (fb.options.kind === 'any' || node.kind === fb.options.kind)
		}),
		node_gone: bool('The node at a place on the Nodes page has not been heard for a while', style('node', 'gone'), [{ type: 'number', id: 'n', label: 'Place (1–8)', default: 1, min: 1, max: 8 }], (fb) => {
			const node = s().nodes?.[fb.options.n - 1]
			return !!node && node.fresh === false
		}),
		node_linked: bool('A caller node is linked to this desk (any number, or at least this many)', style('node', 'caller'), [{ type: 'number', id: 'min', label: 'At least', default: 1, min: 1, max: 16 }], (fb) => (s().linked ?? 0) >= fb.options.min),
		twin_is: bool('The twin is in a state — in step, the main silent, taken over, standing by, or listening', style('twin', 'inStep'), [
			{ type: 'dropdown', id: 'phase', label: 'State', default: 'inStep', choices: [
				{ id: 'inStep', label: 'In step (linked)' }, { id: 'mainSilent', label: 'The main is silent' }, { id: 'tookOver', label: 'This standby took over' },
				{ id: 'listening', label: 'A main, listening' }, { id: 'connecting', label: 'A standby, dialling' }, { id: 'refused', label: 'Refused' },
			] },
		], (fb) => (s().twin?.phase ?? '') === fb.options.phase),
		twin_role_is: bool('This desk is the twin\'s main, or a standby, or has no twin', style('twin', 'standby'), [{ type: 'dropdown', id: 'role', label: 'Role', default: 'standby', choices: [{ id: 'off', label: 'No twin' }, { id: 'main', label: 'The main' }, { id: 'standby', label: 'A standby' }] }], (fb) => (s().twin?.role ?? 'off') === fb.options.role),
		stage_is: bool('The stage timer is running, paused, over or idle', style('stage', 'running'), [{ type: 'dropdown', id: 'phase', label: 'Phase', default: 'running', choices: [{ id: 'running', label: 'Running' }, { id: 'paused', label: 'Paused' }, { id: 'over', label: 'Over' }, { id: 'idle', label: 'Idle' }] }], (fb) => (s().stage?.timer?.phase ?? 'idle') === fb.options.phase),
		stage_colour_is: bool("The stage timer's own colour — what the speaker sees — is green, amber or red", style('stage', 'amber'), [{ type: 'dropdown', id: 'colour', label: 'Colour', default: 'amber', choices: [{ id: 'green', label: 'Green' }, { id: 'amber', label: 'Amber' }, { id: 'red', label: 'Red' }] }], (fb) => {
			const t = s().stage?.timer
			return !!t && t.phase !== 'idle' && (t.colour ?? '') === fb.options.colour
		}),
		stage_pending: bool('A message to the stage is waiting for its ACK (the speaker\'s, the crew\'s, or either)', style('stage', 'pending'), [{ type: 'dropdown', id: 'channel', label: 'Channel', default: 'any', choices: [{ id: 'any', label: 'Either' }, { id: 'speaker', label: 'The speaker' }, { id: 'crew', label: 'The crew' }] }], (fb) => {
			const st = s().stage ?? {}
			const sp = (st.pendingSpeaker ?? '') !== ''
			const cr = (st.pendingCrew ?? '') !== ''
			return fb.options.channel === 'speaker' ? sp : fb.options.channel === 'crew' ? cr : sp || cr
		}),
	}
}
