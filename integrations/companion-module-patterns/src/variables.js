// The variables, as Companion 5 wants them: an object keyed by id. Every id the module writes is
// declared here and the tests hold both lists to each other, so a key never renders its own
// variable's name as its label (track_7 and track_8 did, once).
import { BANK_SIZES } from './state.js'

export function variableDefinitions() {
	const v = {
		air_look: 'The look on air, by name (or empty)',
		preview_look: 'The look loaded in the preview, by name (or empty)',
		pattern: 'What kind of picture is on air (Media, LedWall, ProjectionBlend…)',
	}
	for (let n = 1; n <= BANK_SIZES.look; n++) v[`look_${n}`] = `Look at place ${n} in the show's list (name, or empty)`
	for (let f = 1; f <= BANK_SIZES.look_f; f++) v[`look_f${f}`] = `Look on F${f} (name, or empty)`
	for (let n = 1; n <= 8; n++) {
		v[`lt_${n}`] = `Lower third design ${n} (name, or empty)`
		v[`person_${n}`] = `Person ${n} in the library (name, or empty)`
		v[`stinger_${n}`] = `VOG / stinger ${n} (name, or empty)`
		v[`screen_${n}`] = `Screen ${n} (its label, or empty)`
		v[`track_${n}`] = `Audio playlist track ${n} (name, or empty)`
		v[`screen_${n}_pattern`] = `Screen ${n} — the kind of picture it is showing`
		v[`node_${n}`] = `Node ${n} as the Nodes page lists it — its kind and its machine (or empty)`
		v[`node_${n}_kind`] = `Node ${n} — desk, caller, arcade or timer`
		v[`node_${n}_words`] = `Node ${n} — its own health line`
	}
	for (let n = 1; n <= 6; n++) {
		v[`music_${n}`] = `Break music entry ${n} (name, or empty)`
		v[`section_${n}`] = `Playlist part ${n} (name, or empty)`
	}
	for (let k = 1; k <= BANK_SIZES.cue; k++) {
		v[`cue_${k}`] = `Cue bank ${k} — number and name (1 = the standby cue, 2… the cues after it)`
		v[`cue_${k}_number`] = `Cue bank ${k} — number`
		v[`cue_${k}_name`] = `Cue bank ${k} — name`
	}
	Object.assign(v, {
		blackout: 'Blackout state',
		presenter_step: 'Presenter step number',
		presenter_count: 'Presenter step count',
		playlist: 'Playlist status',
		next_cue: 'Next scheduled cue',
		stinger: 'VOG or stinger on air (name)',
		sting_hold: 'Stinger holding the screens (name)',
		duck: 'Live duck (DUCK/off)',
		lower_third: 'Lower third on screen (name, or empty)',
		lower_third_person: 'The name the lower third on screen carries (or empty)',
		lower_third_preview: 'Lower third in the preview for a sign-off (name, or empty)',
		lower_third_preview_person: 'The name the lower third in the preview carries (or empty)',
		lower_third_default: "The show's default lower third design (★)",
		lower_third_edited: 'EDITED when the design on air differs from the edited one, else off',
		review: 'Review on the multiview (ON/off)',
		weather: 'The weather chip on air (ON/off)',
		weather_text: 'The weather as the desk reads it ("Manchester · 18° · Light rain · wind 12 km/h")',
		weather_place: "The weather chip's place",
		weather_figure: 'The weather chip\'s figure ("18°", "14–19°")',
		weather_view: "The weather chip's view (now / day / tomorrow)",
		freeze: 'Freeze (FROZEN/off)',
		look_state: 'The look on air and whether it still is what is on the screens (LIVE / EDITED / off)',
		look_screens_off: 'How many screens have gone their own way inside the look on air',
		stream_status: "The stream in a word (the desk's own status line)",
		stream_health: "The stream's health (good / slow / trouble)",
		stream_up: 'How long the stream has been up',
		stream_fps: "The stream's frame rate",
		outputs_live: 'The outputs (LIVE/off)',
		edit_safe: 'EDIT SAFE (ON/off)',
		tone: 'The soundcheck tone (ON/off)',
		timing_offset: 'How the day is running against the plan ("11 min late")',
		timing_next_break: 'The next break',
		timing_end: 'When the day is expected to end',
		black: 'Screens faded to black on their own (how many, or off)',
		black_text: 'Screens faded to black on their own, by name ("Screen 2 · Group A")',
		black_audio: "The programme's sound is down with a fade to black (DOWN/off)",
		previous_look: 'The look LOOK BACK returns to (name, or empty)',
		audio_track: 'Audio playlist — the track on (or up next when stopped)',
		audio_next: 'Audio playlist — the track after it',
		audio_n: 'Audio playlist — the place of the track on (1…)',
		audio_count: 'Audio playlist — how many tracks',
		audio_position: 'Audio playlist — where the track is (m:ss)',
		audio_remaining: 'Audio playlist — what is left of the track (m:ss)',
		audio_state: 'Audio playlist (PLAYING / stopped)',
		program: 'What is on air, by name',
		cue_armed: 'Cue stack armed (ARMED/off)',
		cue_hold: 'Cue stack HOLD (HOLD/off)',
		cue_seq: 'Cue stack sequence number',
		cue_standby_number: 'Standby cue number',
		cue_standby_name: 'Standby cue name',
		cue_next_number: 'Next cue number (after the standby)',
		cue_next_name: 'Next cue name',
		cue_previous_number: 'Previous cue number',
		cue_previous_name: 'Previous cue name',
		cue_last_outcome: 'Outcome of the last cue (Done / Failed / Refused…)',
		cue_confirm: 'The confirmation GO is waiting for (or empty)',
		last_error: 'Last ERR line from Patterns',
		music: 'Break music — the track Spotify reports',
		music_state: 'Break music (PLAYING / paused)',
		music_level: 'Break music — the Spotify device level',
		music_device: 'Break music — the Spotify device',
		health: 'Machine health line',
		machine_cpu: 'Machine CPU (percent)',
		machine_fps: 'Output frame rate',
		machine_power: 'Power (mains / BATTERY)',
		machine_advice: 'Machine-page suggestions needing attention',
		web_page: 'The web page on air (its nickname or host, or empty)',
		web_title: "The web page on air — the page's title",
		web_service: 'The web page on air — its service (YouTube, Google Slides, PowerPoint…)',
		web_fps: "The web page on air — frames it delivered in the last second (a video's rate; empty for a still page)",
		audio_routing: 'The routing matrix (which soundtrack goes where): ON or off',
		audio_routing_words: 'The routing matrix in a line — what is routed where and how a VOG behaves',
		web_vt: "The web page on air — its video: armed and where from, or its clock (1:23 / 4:56 · playing), ADVERT while one shows",
		web_armed: 'The armed web VT anywhere on the desk — "VT armed at 1:23" — or empty',
		web_armed_page: 'The page that armed VT is on (its nickname or host), or empty',
		deck_page: 'The deck on air — the page on show (or empty)',
		deck_count: 'The deck on air — how many pages (or empty)',
		deck_file: 'The deck on air — its file (or empty)',
		video_file: 'The clip on air — its file (or empty)',
		video_tag: 'The clip on air — VT, AUDIO, STINGER CLIP or PLAYLIST',
		video_position: 'The clip on air — where it is (m:ss)',
		video_length: 'The clip on air — how long it is (m:ss)',
		video_remaining: 'The clip on air — what is left (m:ss)',
		video_remaining_seconds: 'The clip on air — what is left, in seconds',
		video_text: 'The caller\'s VT clock as the desk reads it',
		video_chip: 'The caller\'s VT clock chip ("VT 2:28")',
		video_call: 'The caller\'s word in the clip\'s last ten seconds ("OUT IN 7")',
		install: 'The install schedule (ON/off)',
		install_programme: 'The install — the programme on',
		install_over: 'The install — the announcement or advert on',
		install_next: 'The install — the next change',
		install_status: "The install page's line",
		clock: 'The clock overlay (ON/off)',
		clock_hours: 'The clock overlay — 12 or 24',
		clock_text: 'The clock overlay — what it reads now',
		clock_seconds: 'The clock overlay — seconds shown (ON/off)',
		clock_date: 'The clock overlay — the date line (ON/off)',
		message: 'The message overlay (ON/off)',
		message_text: 'The message overlay — the words',
		message_scroll: 'The message overlay — SCROLL or still',
		countdown: 'The countdown — running, over or off',
		countdown_text: 'The countdown as the desk reads it ("12:34 · DOORS IN")',
		countdown_remaining_seconds: 'The countdown — what is left, in seconds',
		countdown_label: 'The countdown — the words over the digits',
		countdown_target: 'The countdown — its target (19:30, or 15 min)',
		logo: 'The logo overlay (ON/off)',
		pip: 'The PiP inset (ON/off)',
		overlays_text: 'The overlays in one line',
		desk_version: 'The desk — its version, as it said on the link',
		show: 'The show that is loaded, by name',
		nodes_count: 'How many nodes the desk hears on the beacon right now',
		nodes_text: 'The nodes the desk hears, in one line',
		twin_role: 'The twin — off, main or standby',
		twin_phase: 'The twin — listening, connecting, inStep, mainSilent, tookOver, refused',
		twin_words: "The twin — the Machine page's line",
		twin_holder: 'The twin — the standby that has the show, or empty',
		twin_clocks: 'The twin — each peer\'s clock against this desk\'s',
		stage_timer: 'The stage timer as the speaker reads it',
		stage_phase: 'The stage timer — idle, running, paused or over',
		stage_colour: 'The stage timer — its colour word (green, amber, red)',
		stage_label: 'The stage timer — its label',
		stage_remaining_seconds: 'The stage timer — what is left, in seconds',
		stage_progress: 'The stage timer — how far through, in percent',
		stage_segment: 'The running order — the cue running',
		stage_next: 'The running order — the cue next',
		stage_pending: "The speaker's stage page — a message waiting for its ACK, or empty",
		stage_pending_crew: "The crew's stage page — a message waiting for its ACK, or empty",
	})
	const out = {}
	for (const [id, name] of Object.entries(v)) out[id] = { name }
	return out
}
