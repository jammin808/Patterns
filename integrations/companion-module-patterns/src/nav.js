// The Navigator (round 74): the desk's rails, pages, menus and drawers laid on a fixed set of
// slot keys, so an operator programs the show from the deck — a rail, then its pages, then the
// page's own menu (its looks, cues, designs, screens, its build verbs), then a thing's menu
// (MENU LOOK Walk-in), then a drawer's choices — with HOME, BACK, PREV / NEXT for more entries
// than keys, RUN / MENU mode (a thing's key fires its line, or opens its menu), and FOLLOW both
// ways (the deck turns the desk's pages; the desk's page turns the deck's). Nothing here knows
// a page or a menu ahead of time: every level is asked of the desk (NAV, MENU PAGE <name>,
// MENU <words>) and laid out from the reply, so a page or a verb added on the desk is on the
// deck at once. Pure: it talks through ask() and say() and is tested on a fake wire.

/** How many slot keys the Navigator lays a level on — a Stream Deck XL's 32 less the eight fixed keys. */
export const NAV_SLOTS = 24

export const LEVELS = ['rails', 'pages', 'page', 'menu', 'drawer']

/** The rails as the desk has them; the reply to NAV replaces this the moment it comes. */
export const DEFAULT_RAILS = [
	{ id: 'Show', label: 'SHOW', hue: '#2EE68A', pages: ['Panel', 'Run', 'Eye'] },
	{ id: 'Plan', label: 'PLAN', hue: '#6E9BFF', pages: ['Cues', 'Looks', 'Install'] },
	{ id: 'Build', label: 'BUILD', hue: '#3EC1F3', pages: ['Pattern', 'Media', 'Overlays', 'Lower thirds', 'Countdown', 'Particles', 'Fractals', 'Reactive', 'Branding', 'Layers', 'Library', 'Assistant'] },
	{ id: 'Setup', label: 'SETUP', hue: '#B18CFF', pages: ['Screens', 'Multiview', 'Audio', 'NDI', 'Stream', 'Remote', 'Interactive', 'Arcade', 'Nodes'] },
	{ id: 'Admin', label: 'ADMIN', hue: '#B8E356', pages: ['Machine', 'Help'] },
]

/** Every Navigator variable at its rest value — what a STATE writes before the live Navigator lays its own over them. */
export function navVariableDefaults() {
	return new Navigator().variables()
}

/** The reply's payload as an object, or null: "OK {…}" → {…}; "ERR …" and anything else → null. */
export function payload(reply) {
	const text = String(reply ?? '').trim()
	if (!text.startsWith('OK ')) return null
	try {
		const parsed = JSON.parse(text.slice(3))
		return parsed && typeof parsed === 'object' ? parsed : null
	} catch {
		return null
	}
}

/** A menu's entries as slots: every group's entries in order (a drawer entry opens its children on the next level). */
export function slotsOf(menu) {
	const out = []
	for (const group of menu?.groups ?? []) {
		for (const e of group.entries ?? []) out.push(slotOf(e, group.heading ?? ''))
	}
	return out
}

function slotOf(e, group) {
	const children = Array.isArray(e.children) ? e.children : []
	return {
		id: String(e.id ?? ''),
		text: String(e.text ?? ''),
		detail: String(e.detail ?? ''),
		tone: String(e.tone ?? 'plain'),
		on: e.on === true,
		enabled: e.enabled !== false,
		because: String(e.because ?? ''),
		wire: String(e.wire ?? ''),
		menu: String(e.menu ?? ''),
		takesText: e.takesText === true || String(e.wire ?? '').includes('*'),
		page: String(e.page ?? ''),
		item: String(e.item ?? ''),
		question: String(e.question ?? ''),
		group,
		drawer: children.length > 0,
		children: children.map((c) => slotOf(c, group)),
	}
}

export class Navigator {
	/**
	 * ask(line) → Promise<reply>; say(line) sends and forgets; changed() is called after every change
	 * so the host can rewrite variables, feedbacks and the deck's whereabouts; slots is the key count.
	 */
	constructor({ ask, say, changed = () => {}, log = () => {}, slots = NAV_SLOTS } = {}) {
		this.ask = ask
		this.say = say
		this.changed = changed
		this.log = log
		this.slotCount = slots
		this.rails = DEFAULT_RAILS
		this.pages = DEFAULT_RAILS.flatMap((r) => r.pages.map((header) => ({ header, rail: r.id, hue: r.hue })))
		this.level = 'rails'
		this.rail = null // { id, label, hue, pages }
		this.page = '' // the page's header
		this.menuWords = '' // the words after MENU for the thing on show ('' for the page's own menu)
		this.title = ''
		this.entries = [] // every slot of the level, before paging
		this.offset = 0
		this.crumbs = [] // where the deck is, as words for NAV DECK and the title key
		this.trail = [] // the levels behind this one, for BACK
		this.mode = 'menu' // 'menu': a thing's key opens its menu; 'run': it fires its line
		this.follow = false // the deck and the desk turn each other's pages
		this.text = '' // the words a text-taking key uses in place of its *
		this.reply = '' // the desk's last answer to a key, for the title key
		this.deskPage = '' // where the desk is, from STATE
		this.layRails() // the built-in rails on the keys from the first moment; the desk's own table replaces them when NAV answers
	}

	/** The slots on the keys now: the level's entries from the offset, one per key. */
	get slots() {
		return this.entries.slice(this.offset, this.offset + this.slotCount)
	}

	get count() {
		return this.entries.length
	}

	/** "1–24 of 40", or "" when everything fits. */
	get range() {
		if (this.entries.length <= this.slotCount) return ''
		const from = this.offset + 1
		const to = Math.min(this.offset + this.slotCount, this.entries.length)
		return `${from}–${to} of ${this.entries.length}`
	}

	get where() {
		return this.crumbs.length > 0 ? this.crumbs.join(' › ') : 'RAILS'
	}

	/** The reply to NAV: the rails and their pages replace the built-in table. */
	learnDesk(nav) {
		if (!nav || typeof nav !== 'object') return
		const rails = Array.isArray(nav.rails) ? nav.rails : []
		if (rails.length > 0) {
			this.rails = rails.map((r) => ({ id: String(r.id ?? ''), label: String(r.label ?? r.id ?? ''), hue: String(r.hue ?? ''), pages: Array.isArray(r.pages) ? r.pages.map(String) : [] }))
		}
		const pages = Array.isArray(nav.pages) ? nav.pages : []
		if (pages.length > 0) this.pages = pages.map((p) => ({ header: String(p.header ?? ''), rail: String(p.rail ?? ''), hue: String(p.hue ?? ''), settings: p.settings === true }))
		this.deskPage = String(nav.desk?.page ?? this.deskPage)
		if (this.level === 'rails') this.home()
	}

	/** Where the desk is, from every STATE: with FOLLOW on, the deck turns to the desk's page. */
	async deskMoved(page, run) {
		const header = run ? 'Run' : String(page ?? '')
		const moved = header !== this.deskPage
		this.deskPage = header
		if (!moved || !this.follow || header.length === 0) return
		if (this.level === 'rails' || this.level === 'pages' || this.page === header) return
		await this.openPage(header, { fromDesk: true })
	}

	// ---- the levels ---------------------------------------------------------------------------

	home() {
		this.layRails()
		this.settle()
	}

	layRails() {
		this.level = 'rails'
		this.rail = null
		this.page = ''
		this.menuWords = ''
		this.title = 'RAILS'
		this.crumbs = []
		this.trail = []
		this.offset = 0
		this.entries = this.rails.map((r) => ({ id: 'rail:' + r.id, text: r.label, detail: r.pages.join(' · '), tone: 'go', on: this.pages.find((p) => p.header === this.deskPage)?.rail === r.id, enabled: true, because: '', wire: '', menu: '', takesText: false, page: '', item: '', question: '', group: 'RAILS', drawer: false, children: [], rail: r }))
	}

	openRail(idOrLabel) {
		const word = String(idOrLabel ?? '').trim().toLowerCase()
		const rail = this.rails.find((r) => r.id.toLowerCase() === word || r.label.toLowerCase() === word)
		if (!rail) return false
		this.push()
		this.level = 'pages'
		this.rail = rail
		this.page = ''
		this.menuWords = ''
		this.title = rail.label
		this.crumbs = [rail.label]
		this.offset = 0
		this.entries = rail.pages.map((header) => ({ id: 'page:' + header, text: header, detail: '', tone: 'go', on: header === this.deskPage, enabled: true, because: '', wire: 'NAV ' + header, menu: '', takesText: false, page: header, item: '', question: '', group: rail.label, drawer: false, children: [], header }))
		this.settle()
		return true
	}

	/** A page's own menu: MENU PAGE <header>, laid out; with FOLLOW on (and not because the desk moved), the desk turns to it too. */
	async openPage(header, { fromDesk = false } = {}) {
		const name = String(header ?? '').trim()
		const page = this.pages.find((p) => p.header.toLowerCase() === name.toLowerCase())
		if (!page) return false
		const rail = this.rails.find((r) => r.id === page.rail) ?? this.rail
		const menu = payload(await this.ask(`MENU PAGE ${page.header}`))
		if (!menu) return false
		this.push()
		this.level = 'page'
		this.rail = rail
		this.page = page.header
		this.menuWords = ''
		this.title = page.header.toUpperCase()
		this.crumbs = [rail?.label ?? '', page.header].filter((w) => w.length > 0)
		this.offset = 0
		this.entries = slotsOf(menu)
		if (this.follow && !fromDesk && this.deskPage !== page.header) this.say(`NAV ${page.header}`)
		this.settle()
		return true
	}

	/** A thing's own menu (MENU LOOK Walk-in, MENU CUE 03.020, MENU SCREEN 2 …), laid out. */
	async openMenu(words) {
		const w = String(words ?? '').trim()
		if (w.length === 0) return false
		const menu = payload(await this.ask(`MENU ${w}`))
		if (!menu) return false
		this.push()
		this.level = 'menu'
		this.menuWords = w
		this.title = String(menu.title ?? w)
		this.crumbs = [...this.crumbs.slice(0, 2), this.title]
		this.offset = 0
		this.entries = slotsOf(menu)
		this.settle()
		return true
	}

	/** A drawer's choices (a look's F-key, a cue's transition, a preset's kinds…), laid out. */
	openDrawer(slot) {
		if (!slot?.drawer) return false
		this.push()
		this.level = 'drawer'
		this.title = slot.text
		this.crumbs = [...this.crumbs, slot.text]
		this.offset = 0
		this.entries = slot.children
		this.settle()
		return true
	}

	/** BACK: the level before, asked again so its ticks are fresh. */
	async back() {
		const before = this.trail.pop()
		if (!before) {
			this.home()
			return true
		}
		// The levels behind the one we return to, kept aside: opening it again would push the level we are leaving.
		const behind = [...before.trail]
		switch (before.level) {
			case 'rails':
				this.home()
				break
			case 'pages':
				if (!this.openRail(before.rail?.id ?? '')) this.home()
				break
			case 'page':
				if (!(await this.openPage(before.page, { fromDesk: true }))) this.home()
				break
			case 'menu':
				if (!(await this.openMenu(before.menuWords))) this.home()
				break
			case 'drawer':
				this.level = 'drawer'
				this.title = before.title
				this.crumbs = before.crumbs
				this.entries = before.entries
				break
			default:
				this.home()
				break
		}
		if (this.level !== 'rails') {
			this.trail = behind
			this.offset = Math.min(before.offset, Math.max(0, this.entries.length - 1))
			this.clampOffset()
			this.settle()
		}
		return true
	}

	/** The level asked again: after a press, so a tick that moved (the look on air, the standby) shows at once. */
	async refresh() {
		switch (this.level) {
			case 'rails': this.home(); return
			case 'pages': { const rail = this.rail; this.trail.pop(); this.openRail(rail?.id ?? ''); return }
			case 'page': {
				const menu = payload(await this.ask(`MENU PAGE ${this.page}`))
				if (menu) { this.entries = slotsOf(menu); this.clampOffset(); this.settle() }
				return
			}
			case 'menu': {
				const menu = payload(await this.ask(`MENU ${this.menuWords}`))
				if (menu) { this.entries = slotsOf(menu); this.clampOffset(); this.settle() }
				return
			}
			default:
				return
		}
	}

	next() {
		if (this.offset + this.slotCount >= this.entries.length) return false
		this.offset += this.slotCount
		this.settle()
		return true
	}

	prev() {
		if (this.offset === 0) return false
		this.offset = Math.max(0, this.offset - this.slotCount)
		this.settle()
		return true
	}

	setMode(mode) {
		const m = String(mode ?? '').toLowerCase()
		this.mode = m === 'toggle' ? (this.mode === 'run' ? 'menu' : 'run') : m === 'run' ? 'run' : 'menu'
		this.settle()
	}

	setFollow(mode) {
		const m = String(mode ?? '').toLowerCase()
		this.follow = m === 'toggle' ? !this.follow : m === 'on' || m === 'true'
		this.settle()
	}

	setText(words) {
		this.text = String(words ?? '').trim()
		this.settle()
	}

	// ---- a key pressed -------------------------------------------------------------------------

	/**
	 * The slot key n (1-based) pressed. A rail opens its pages; a page opens its menu; a drawer opens
	 * its choices; in MENU mode a thing with a menu opens it; otherwise the thing's line goes to the
	 * desk (with the deck's words in place of a *), and the level is asked again so its ticks move.
	 * Returns the words for the log: what was sent, or why nothing was.
	 */
	async press(n) {
		const slot = this.slots[n - 1]
		if (!slot) return 'nothing on that key'
		if (slot.rail) { this.openRail(slot.rail.id); return `rail ${slot.rail.label}` }
		if (slot.header) { await this.openPage(slot.header); return `page ${slot.header}` }
		if (slot.drawer) { this.openDrawer(slot); return `drawer ${slot.text}` }
		if (this.mode === 'menu' && slot.menu.length > 0) { await this.openMenu(slot.menu); return `menu ${slot.menu}` }
		if (!slot.enabled) { this.reply = slot.because; this.settle(); return slot.because || 'not now' }
		let line = slot.wire
		if (line.length === 0 && slot.page.length > 0) line = `NAV ${slot.page}${slot.item ? ' ' + slot.item : ''}`
		if (line.length === 0) { this.reply = slot.question ? 'Ask it on the desk' : 'Only on the desk'; this.settle(); return this.reply }
		if (slot.takesText) {
			if (this.text.length === 0) { this.reply = 'Type the words first (NAV TEXT)'; this.settle(); return this.reply }
			line = line.replace('*', this.text)
		}
		const reply = String(await this.ask(line))
		this.reply = reply.startsWith('OK') ? reply.slice(2).trim() || 'OK' : reply
		this.settle() // the desk's answer on the title key at once; the level's ticks follow when it has been asked again
		await this.refresh()
		return line
	}

	// ---- inside ----------------------------------------------------------------------------------

	push() {
		this.trail.push({ level: this.level, rail: this.rail, page: this.page, menuWords: this.menuWords, title: this.title, crumbs: this.crumbs, entries: this.entries, offset: this.offset, trail: [...this.trail] })
		if (this.trail.length > 16) this.trail.shift()
	}

	clampOffset() {
		if (this.offset >= this.entries.length) this.offset = Math.max(0, this.entries.length - this.slotCount)
		if (this.offset < 0) this.offset = 0
	}

	settle() {
		this.changed(this)
	}

	/** The variables a key reads, for every slot and the level. */
	variables() {
		const v = {
			nav_level: this.level,
			nav_rail: this.rail?.label ?? '',
			nav_page: this.page,
			nav_menu: this.menuWords,
			nav_title: this.title || 'RAILS',
			nav_where: this.where,
			nav_range: this.range,
			nav_count: String(this.entries.length),
			nav_mode: this.mode === 'run' ? 'RUN' : 'MENU',
			nav_follow: this.follow ? 'FOLLOW' : 'off',
			nav_text: this.text,
			nav_reply: this.reply,
			nav_pages: (this.rail?.pages ?? []).join(' · '),
		}
		const slots = this.slots
		for (let n = 1; n <= this.slotCount; n++) {
			const s = slots[n - 1]
			v[`nav_slot_${n}`] = s ? `${s.text}${s.drawer ? ' ▸' : s.takesText ? ' …' : ''}` : ''
			v[`nav_slot_${n}_wire`] = s?.wire ?? ''
			v[`nav_slot_${n}_detail`] = s ? (s.enabled ? s.detail : s.because) : ''
			v[`nav_slot_${n}_group`] = s?.group ?? ''
		}
		return v
	}
}
