// AutoCopy for macOS — select text and it is already on the clipboard. © 2026 ODIZH
// Mac counterpart of the Windows AutoCopy.cs. Build: ./build.sh
import Cocoa
import SwiftUI
import Carbon.HIToolbox
import ServiceManagement
import ApplicationServices
import os

let appVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
let homePage = "https://github.com/ua50/autocopy"

// Diagnostics without clipboard contents: `log show --predicate 'subsystem == "ua.uran.autocopy"' --last 10m`
let logger = Logger(subsystem: "ua.uran.autocopy", category: "main")
func dlog(_ s: String) { logger.notice("\(s, privacy: .public)") }

// ───────── settings (~/Library/Application Support/AutoCopy/settings.txt) ─────────
final class Config {
    var enabled = true
    var sound = true
    var popup = true
    var rightPanel = true
    var excluded: [String] = Config.defaultExcluded
    var parked: [String] = []

    // Default "do not copy" list (app executable names, as in Activity Monitor).
    // Finder is here because a rubber-band drag + Cmd+C copies FILES, not text.
    static let defaultExcluded = ["Finder", "Terminal", "iTerm2", "Microsoft Excel", "AnyDesk", "kicad", "Screen Sharing"]

    let url: URL = {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("AutoCopy", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("settings.txt")
    }()

    init() { load() }

    func load() {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { save(); return }
        for raw in text.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }
            guard let eq = line.firstIndex(of: "=") else { continue }
            let key = line[..<eq].trimmingCharacters(in: .whitespaces).lowercased()
            let val = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
            switch key {
            case "enabled": enabled = val == "1"
            case "sound": sound = val == "1"
            case "popup": popup = val == "1"
            case "right_panel": rightPanel = val == "1"
            case "exclude": excluded = Config.list(val)
            case "parked": parked = Config.list(val)
            default: break
            }
        }
    }

    static func list(_ s: String) -> [String] {
        s.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    func save() {
        let b: (Bool) -> String = { $0 ? "1" : "0" }
        let text = """
        # AutoCopy — налаштування (можна редагувати вручну, потім перезапустити програму)
        enabled=\(b(enabled))
        sound=\(b(sound))
        popup=\(b(popup))
        # 1 = біля правого кліку над текстом показувати панель «1 / 2»
        right_panel=\(b(rightPanel))
        # назви програм (як у Моніторі активності), через кому
        exclude=\(excluded.joined(separator: ", "))
        # програми, де копіювання працює, але які збережені для швидкого повернення в exclude
        parked=\(parked.joined(separator: ", "))

        """
        do { try text.write(to: url, atomically: true, encoding: .utf8) }
        catch { NSLog("AutoCopy: cannot save settings: \(error)") }
    }

    func isExcluded(_ name: String) -> Bool {
        excluded.contains { $0.caseInsensitiveCompare(name) == .orderedSame }
    }
}

// ───────── two last copied texts (memory only, never written to disk) ─────────
final class ClipRing {
    private(set) var items: [String] = [] // [0] = last, [1] = previous
    func add(_ text: String) {
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return }
        items.removeAll { $0 == text }
        items.insert(text, at: 0)
        if items.count > 2 { items.removeLast() }
    }
    static func preview(_ s: String) -> String {
        let one = s.replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\t", with: " ").trimmingCharacters(in: .whitespaces)
        return one.count > 60 ? String(one.prefix(60)) + "…" : one
    }
}

// ───────── helpers ─────────
func appName(_ app: NSRunningApplication?) -> String {
    guard let app else { return "" }
    return app.executableURL?.lastPathComponent ?? app.localizedName ?? ""
}

// CG/AX coordinates have the origin at the top-left of the main screen, AppKit at the bottom-left.
func toAppKit(_ p: CGPoint) -> NSPoint {
    let h = NSScreen.screens.first?.frame.height ?? 0
    return NSPoint(x: p.x, y: h - p.y)
}

func postKey(_ key: Int, flags: CGEventFlags = []) {
    let src = CGEventSource(stateID: .hidSystemState)
    for down in [true, false] {
        guard let e = CGEvent(keyboardEventSource: src, virtualKey: CGKeyCode(key), keyDown: down) else { continue }
        e.flags = flags
        e.post(tap: .cghidEventTap)
    }
}

enum AX {
    static func attr(_ el: AXUIElement, _ name: String) -> (AXError, CFTypeRef?) {
        var v: CFTypeRef?
        let e = AXUIElementCopyAttributeValue(el, name as CFString, &v)
        return (e, v)
    }
    static func role(_ el: AXUIElement) -> String {
        (attr(el, kAXRoleAttribute).1 as? String) ?? ""
    }
    static func element(at p: CGPoint) -> AXUIElement? {
        let sys = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(sys, 0.1)
        var el: AXUIElement?
        guard AXUIElementCopyElementAtPosition(sys, Float(p.x), Float(p.y), &el) == .success else { return nil }
        return el
    }
    static let editableRoles: Set<String> = ["AXTextArea", "AXTextField", "AXComboBox"]
    // Places where a drag is not a text selection (title bar, buttons, scroll bars…).
    static let nonTextRoles: Set<String> = ["AXButton", "AXWindow", "AXToolbar", "AXScrollBar", "AXValueIndicator",
        "AXMenuBar", "AXMenuBarItem", "AXSplitter", "AXTabGroup", "AXRadioButton", "AXCheckBox", "AXSlider",
        "AXPopUpButton", "AXMenuButton", "AXDisclosureTriangle", "AXImage", "AXDockItem"]
}

// ───────── dark row panel «1 … / 2 …» and the «✓ Скопійовано» hint ─────────
final class RowsView: NSView {
    var rows: [String] = [] { didSet { needsDisplay = true } }
    static let font = NSFont.systemFont(ofSize: 13)
    static let rowH: CGFloat = 30, padX: CGFloat = 12
    override var isFlipped: Bool { true }

    func fittingSize() -> NSSize {
        let w = rows.map { ($0 as NSString).size(withAttributes: [.font: RowsView.font]).width }.max() ?? 0
        return NSSize(width: ceil(w) + RowsView.padX * 2 + 2, height: CGFloat(rows.count) * RowsView.rowH + CGFloat(max(rows.count - 1, 0)) + 2)
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor(white: 0.125, alpha: 1).setFill()
        NSBezierPath(roundedRect: bounds, xRadius: 6, yRadius: 6).fill()
        let attrs: [NSAttributedString.Key: Any] = [.font: RowsView.font, .foregroundColor: NSColor.white]
        for (i, text) in rows.enumerated() {
            let r = NSRect(x: 1, y: 1 + CGFloat(i) * (RowsView.rowH + 1), width: bounds.width - 2, height: RowsView.rowH)
            NSColor(white: 0.18, alpha: 1).setFill()
            NSBezierPath(roundedRect: r, xRadius: 5, yRadius: 5).fill()
            let h = (text as NSString).size(withAttributes: attrs).height
            (text as NSString).draw(at: NSPoint(x: r.minX + RowsView.padX, y: r.minY + (r.height - h) / 2), withAttributes: attrs)
        }
    }

    func row(atWindowPoint p: NSPoint) -> Int {
        let y = bounds.height - p.y // window coords are bottom-up, the view is flipped
        let i = Int((y - 1) / (RowsView.rowH + 1))
        return (p.x >= 0 && p.x <= bounds.width && y >= 0 && i >= 0 && i < rows.count) ? i : -1
    }
}

final class FloatingPanel: NSPanel {
    let rowsView = RowsView()
    init() {
        super.init(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        ignoresMouseEvents = true // clicks are handled in the event tap, so the app's context menu stays open
        level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.popUpMenuWindow)) + 1)
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
        contentView = rowsView
    }
    override var canBecomeKey: Bool { false }

    func show(rows: [String], topRight: NSPoint? = nil, topLeft: NSPoint? = nil) {
        rowsView.rows = rows
        let size = rowsView.fittingSize()
        var origin = NSPoint.zero
        if let tr = topRight { origin = NSPoint(x: tr.x - size.width, y: tr.y - size.height) }
        if let tl = topLeft { origin = NSPoint(x: tl.x, y: tl.y - size.height) }
        if let screen = NSScreen.screens.first(where: { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) }) {
            let f = screen.visibleFrame
            origin.x = min(max(origin.x, f.minX), f.maxX - size.width)
            origin.y = min(max(origin.y, f.minY), f.maxY - size.height)
        }
        setFrame(NSRect(origin: origin, size: size), display: true)
        orderFrontRegardless()
    }

    func row(atScreen p: NSPoint) -> Int {
        guard isVisible, frame.contains(p) else { return -1 }
        return rowsView.row(atWindowPoint: NSPoint(x: p.x - frame.minX, y: p.y - frame.minY))
    }
}

// ───────── «Де не копіювати…» window ─────────
final class ExclModel: ObservableObject {
    @Published var excluded: [String]
    @Published var parked: [String]
    @Published var selEx: String?
    @Published var selPk: String?
    @Published var input = ""
    init(_ ex: [String], _ pk: [String]) { excluded = ex; parked = pk }

    func toParked() { guard let s = selEx else { return }; excluded.removeAll { $0 == s }; if !parked.contains(s) { parked.append(s) }; selEx = nil }
    func toExcluded() { guard let s = selPk else { return }; parked.removeAll { $0 == s }; if !excluded.contains(s) { excluded.append(s) }; selPk = nil }
    func add() {
        let s = input.trimmingCharacters(in: .whitespaces)
        guard !s.isEmpty else { return }
        parked.removeAll { $0.caseInsensitiveCompare(s) == .orderedSame }
        if !excluded.contains(where: { $0.caseInsensitiveCompare(s) == .orderedSame }) { excluded.append(s) }
        input = ""
    }
    func removeSelected() {
        if let s = selEx { excluded.removeAll { $0 == s }; selEx = nil }
        if let s = selPk { parked.removeAll { $0 == s }; selPk = nil }
    }
}

struct ExclView: View {
    @ObservedObject var m: ExclModel
    let running: [String]
    let onSave: () -> Void
    let onCancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Ліворуч — програми, де AutoCopy НЕ копіює. В усіх інших копіює.\nПраворуч «Виключення» — програми, яким ви зняли обмеження: вони копіюють, але тут збережені, щоб повернути ліворуч одним кліком (стрілки).")
                .fixedSize(horizontal: false, vertical: true)
            HStack(alignment: .center, spacing: 8) {
                VStack(alignment: .leading) {
                    Text("Не копіює:")
                    List(m.excluded, id: \.self, selection: $m.selEx) { Text($0) }.frame(minHeight: 180)
                }
                VStack(spacing: 8) {
                    Button("→") { m.toParked() }.help("Зняти обмеження: у цій програмі автокопіювання працюватиме").disabled(m.selEx == nil)
                    Button("←") { m.toExcluded() }.help("Повернути в «Не копіює»").disabled(m.selPk == nil)
                }
                VStack(alignment: .leading) {
                    Text("Виключення (копіює):")
                    List(m.parked, id: \.self, selection: $m.selPk) { Text($0) }.frame(minHeight: 180)
                }
            }
            Text("Додати в «Не копіює» (оберіть відкриту програму або впишіть назву):")
            HStack {
                TextField("Назва програми", text: $m.input).onSubmit { m.add() }
                Menu("Відкриті програми") { ForEach(running, id: \.self) { n in Button(n) { m.input = n } } }.frame(width: 170)
                Button("Додати") { m.add() }
            }
            HStack {
                Button("Видалити вибране") { m.removeSelected() }.disabled(m.selEx == nil && m.selPk == nil)
                Button("За замовчуванням") { m.excluded = Config.defaultExcluded }
                    .help("Стандартний список «Не копіює» (Finder, термінали, Excel, AnyDesk…)")
                Spacer()
                Button("Скасувати", action: onCancel).keyboardShortcut(.cancelAction)
                Button("Зберегти", action: onSave).keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(minWidth: 600, minHeight: 430)
    }
}

// ───────── the app ─────────
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let cfg = Config()
    let ring = ClipRing()
    var statusItem: NSStatusItem!
    var tap: CFMachPort?
    var permissionTimer: Timer?
    let pickPanel = FloatingPanel()
    let copiedPanel = FloatingPanel()
    var exclWindow: NSWindow?
    var hotKeyRef: EventHotKeyRef?
    var hotKeyOk = false

    // mouse state
    var downPt = CGPoint.zero
    var copyWork: DispatchWorkItem?
    var panelWork: DispatchWorkItem?
    var panelDownRow = -1
    var lastChangeCount = NSPasteboard.general.changeCount
    var manualAXDone = Set<pid_t>()

    func applicationDidFinishLaunching(_ n: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
        updateIcon()

        dlog("AutoCopy \(appVersion) started, trusted=\(AXIsProcessTrusted())")
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        if !startTap(prompt: opts) {
            // no Accessibility permission yet — retry quietly until it is granted
            permissionTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] t in
                guard let self else { return }
                if self.startTap(prompt: nil) { t.invalidate(); self.updateIcon() }
            }
        }
        Timer.scheduledTimer(withTimeInterval: 0.4, repeats: true) { [weak self] _ in self?.pollClipboard() }
        registerHotKey()
    }

    // ── event tap: needs «Універсальний доступ» (Accessibility) ──
    @discardableResult
    func startTap(prompt: CFDictionary?) -> Bool {
        guard AXIsProcessTrustedWithOptions(prompt) else {
            if prompt != nil { dlog("not trusted yet (Accessibility)") }
            return false
        }
        let mask: CGEventMask = (1 << CGEventType.leftMouseDown.rawValue) | (1 << CGEventType.leftMouseUp.rawValue)
            | (1 << CGEventType.rightMouseDown.rawValue) | (1 << CGEventType.rightMouseUp.rawValue)
            | (1 << CGEventType.otherMouseDown.rawValue)
        let cb: CGEventTapCallBack = { _, type, event, refcon in
            let me = Unmanaged<AppDelegate>.fromOpaque(refcon!).takeUnretainedValue()
            return me.handle(type, event)
        }
        guard let t = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                        eventsOfInterest: mask, callback: cb,
                                        userInfo: Unmanaged.passUnretained(self).toOpaque()) else {
            dlog("trusted, but tapCreate failed")
            return false
        }
        dlog("event tap started")
        tap = t
        CFRunLoopAddSource(CFRunLoopGetMain(), CFMachPortCreateRunLoopSource(nil, t, 0), .commonModes)
        CGEvent.tapEnable(tap: t, enable: true)
        return true
    }

    // Runs on the main thread for every click in the system: only remember and schedule, never block.
    func handle(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            return Unmanaged.passUnretained(event)
        }
        let loc = event.location
        let ns = toAppKit(loc)

        // Clicks on our «1 / 2» panel are swallowed so the app's own context menu does not get them.
        if pickPanel.isVisible {
            let row = pickPanel.row(atScreen: ns)
            if row >= 0 && type == .leftMouseDown { panelDownRow = row; return nil }
            if row >= 0 && type == .leftMouseUp {
                if row == panelDownRow { let r = row; DispatchQueue.main.async { self.pickFromPanel(r) } }
                panelDownRow = -1
                return nil
            }
            if type == .leftMouseDown || type == .rightMouseDown || type == .otherMouseDown { hidePanel() }
        }
        guard cfg.enabled else { return Unmanaged.passUnretained(event) }

        switch type {
        case .leftMouseDown:
            copyWork?.cancel()
            downPt = loc
        case .leftMouseUp:
            let mods: CGEventFlags = [.maskCommand, .maskAlternate, .maskShift, .maskControl]
            let dragged = abs(loc.x - downPt.x) >= 6 || abs(loc.y - downPt.y) >= 6
            let multi = event.getIntegerValueField(.mouseEventClickState) >= 2
            if dragged || multi { dlog("mouse up: dragged=\(dragged) multi=\(multi) flags=\(event.flags.rawValue)") }
            if (dragged || multi) && event.flags.intersection(mods).isEmpty {
                let start = downPt
                let w = DispatchWorkItem { [weak self] in self?.doCopy(downAt: start, upAt: loc) }
                copyWork = w
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.1, execute: w)
            }
        case .rightMouseDown:
            if cfg.rightPanel && ring.items.count >= 2 {
                panelWork?.cancel()
                let w = DispatchWorkItem { [weak self] in self?.maybeShowPanel(at: loc) }
                panelWork = w
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.15, execute: w) // let the context menu appear first
            }
        default: break
        }
        return Unmanaged.passUnretained(event)
    }

    func frontAllowed() -> NSRunningApplication? {
        guard let app = NSWorkspace.shared.frontmostApplication else { return nil }
        if app.processIdentifier == ProcessInfo.processInfo.processIdentifier { return nil }
        if cfg.isExcluded(appName(app)) { return nil }
        return app
    }

    enum Selection { case some, none, unknown }

    // Asks the app through Accessibility whether something is selected, so we don't press Cmd+C
    // on nothing (Cocoa apps beep when Copy is disabled).
    func selection(in app: NSRunningApplication) -> Selection {
        let pid = app.processIdentifier
        let axApp = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(axApp, 0.15)
        if !manualAXDone.contains(pid) { // Electron apps expose their text only after this flag
            AXUIElementSetAttributeValue(axApp, "AXManualAccessibility" as CFString, kCFBooleanTrue)
            manualAXDone.insert(pid)
        }
        let (e, f) = AX.attr(axApp, kAXFocusedUIElementAttribute)
        guard e == .success, let f, CFGetTypeID(f) == AXUIElementGetTypeID() else { return .unknown }
        let focused = f as! AXUIElement
        let (e2, v) = AX.attr(focused, kAXSelectedTextAttribute)
        guard e2 == .success, let s = v as? String else { return .unknown }
        if !s.isEmpty { return .some }
        // Web pages report "" for non-editable selections, so an empty answer is trusted only in text fields.
        return AX.editableRoles.contains(AX.role(focused)) ? .none : .unknown
    }

    func doCopy(downAt: CGPoint, upAt: CGPoint) {
        guard cfg.enabled, !IsSecureEventInputEnabled() else { dlog("skip: disabled or secure input"); return } // password field
        if CGEventSource.buttonState(.combinedSessionState, button: .left) { dlog("skip: button still down"); return }
        guard let app = frontAllowed() else { dlog("skip: front app excluded/self: \(appName(NSWorkspace.shared.frontmostApplication))"); return }
        let sel = selection(in: app)
        switch sel {
        case .none: dlog("skip: \(appName(app)) says nothing selected"); return
        case .some: break
        case .unknown:
            if let el = AX.element(at: downAt), AX.nonTextRoles.contains(AX.role(el)) {
                dlog("skip: drag started on \(AX.role(el))"); return
            }
        }
        let before = NSPasteboard.general.changeCount
        postKey(kVK_ANSI_C, flags: .maskCommand)
        dlog("Cmd+C sent to \(appName(app)) (selection=\(sel))")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
            guard let self else { return }
            let pb = NSPasteboard.general
            guard pb.changeCount != before, pb.string(forType: .string) != nil else { dlog("clipboard did not change"); return }
            dlog("copied OK")
            if self.cfg.sound, let s = NSSound(named: "Tink") { s.volume = 0.3; s.play() }
            if self.cfg.popup { self.showCopied(at: toAppKit(upAt)) }
        }
    }

    func showCopied(at p: NSPoint) {
        copiedPanel.show(rows: ["✓ Скопійовано"], topLeft: NSPoint(x: p.x + 14, y: p.y - 14))
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.7) { [weak self] in self?.copiedPanel.orderOut(nil) }
    }

    // ── clipboard ring: macOS has no "clipboard changed" event, so poll the change counter ──
    func pollClipboard() {
        let pb = NSPasteboard.general
        guard pb.changeCount != lastChangeCount else { return }
        lastChangeCount = pb.changeCount
        let types = pb.types ?? []
        // password managers mark secrets with these types (nspasteboard.org) — never keep them
        if types.contains(NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")) ||
           types.contains(NSPasteboard.PasteboardType("org.nspasteboard.TransientType")) { return }
        if let s = pb.string(forType: .string) { ring.add(s) }
    }

    func rowLabels() -> [String] {
        ring.items.enumerated().map { "\($0.offset + 1)   \(ClipRing.preview($0.element))" }
    }

    func setClipboard(_ s: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(s, forType: .string)
        ring.add(s) // picking "2" makes it the last one, like on Windows
        lastChangeCount = pb.changeCount
    }

    // ── right-click panel ──
    func maybeShowPanel(at p: CGPoint) {
        guard cfg.enabled, cfg.rightPanel, ring.items.count >= 2, frontAllowed() != nil,
              let el = AX.element(at: p), AX.editableRoles.contains(AX.role(el)) else { return }
        let ns = toAppKit(p)
        pickPanel.show(rows: rowLabels(), topRight: NSPoint(x: ns.x - 6, y: ns.y + 4))
        panelWork = DispatchWorkItem { [weak self] in self?.hidePanel() }
        DispatchQueue.main.asyncAfter(deadline: .now() + 7, execute: panelWork!)
    }

    func hidePanel() {
        if pickPanel.isVisible { pickPanel.orderOut(nil) }
    }

    func pickFromPanel(_ row: Int) {
        hidePanel()
        guard row < ring.items.count else { return }
        let text = ring.items[row]
        postKey(kVK_Escape) // close the app's context menu
        setClipboard(text)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { postKey(kVK_ANSI_V, flags: .maskCommand) }
    }

    // ── Ctrl+Option+V: menu at the cursor «1 … / 2 …» ──
    func registerHotKey() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, ud in
            let me = Unmanaged<AppDelegate>.fromOpaque(ud!).takeUnretainedValue()
            DispatchQueue.main.async { me.hotKeyPressed() }
            return noErr
        }, 1, &spec, Unmanaged.passUnretained(self).toOpaque(), nil)
        let id = EventHotKeyID(signature: OSType(0x41435056) /* ACPV */, id: 1)
        hotKeyOk = RegisterEventHotKey(UInt32(kVK_ANSI_V), UInt32(controlKey | optionKey), id,
                                       GetApplicationEventTarget(), 0, &hotKeyRef) == noErr
    }

    func hotKeyPressed() {
        let prev = NSWorkspace.shared.frontmostApplication
        let menu = NSMenu()
        menu.autoenablesItems = false
        if ring.items.isEmpty {
            let it = NSMenuItem(title: "Буфер ще порожній", action: nil, keyEquivalent: "")
            it.isEnabled = false
            menu.addItem(it)
        }
        var picked: String?
        for (i, label) in rowLabels().enumerated() {
            let it = ClosureItem(title: label, key: "\(i + 1)") { [weak self] in picked = self?.ring.items[i] }
            menu.addItem(it)
        }
        NSApp.activate(ignoringOtherApps: true) // so the menu gets the keyboard (1, 2, arrows, Esc)
        menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
        prev?.activate()
        guard let text = picked else { return }
        setClipboard(text)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { postKey(kVK_ANSI_V, flags: .maskCommand) }
    }

    // ── menu bar ──
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let trusted = AXIsProcessTrusted() && tap != nil
        if !trusted {
            menu.addItem(ClosureItem(title: "⚠︎ Дати дозвіл «Універсальний доступ»…") {
                NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
            })
            menu.addItem(.separator())
        }
        menu.addItem(toggle("Автокопіювання увімкнено", cfg.enabled) { [weak self] in self?.cfg.enabled.toggle() })
        menu.addItem(toggle("Звук при копіюванні", cfg.sound) { [weak self] in self?.cfg.sound.toggle() })
        menu.addItem(toggle("Підказка «Скопійовано»", cfg.popup) { [weak self] in self?.cfg.popup.toggle() })
        menu.addItem(toggle("Панель вибору біля правого кліку над текстом", cfg.rightPanel) { [weak self] in
            self?.cfg.rightPanel.toggle(); self?.hidePanel()
        })
        menu.addItem(.separator())
        menu.addItem(ClosureItem(title: "Де не копіювати…") { [weak self] in self?.showExclusions() })
        let login = SMAppService.mainApp.status == .enabled
        let li = ClosureItem(title: "Запускати разом з Mac") { [weak self] in self?.toggleLogin() }
        li.state = login ? .on : .off
        menu.addItem(li)
        if !hotKeyOk {
            let it = NSMenuItem(title: "Ctrl+Option+V зайнято іншою програмою", action: nil, keyEquivalent: "")
            it.isEnabled = false
            menu.addItem(it)
        }
        menu.addItem(.separator())
        menu.addItem(ClosureItem(title: "Про AutoCopy…") { [weak self] in self?.showAbout() })
        menu.addItem(ClosureItem(title: "Вихід", key: "q") { NSApp.terminate(nil) })
    }

    func toggle(_ title: String, _ on: Bool, _ change: @escaping () -> Void) -> NSMenuItem {
        let it = ClosureItem(title: title) { [weak self] in change(); self?.cfg.save(); self?.updateIcon() }
        it.state = on ? .on : .off
        return it
    }

    func showAbout() {
        let p = NSMutableParagraphStyle()
        p.alignment = .center
        let base: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 11), .paragraphStyle: p,
                                                   .foregroundColor: NSColor.labelColor]
        let credits = NSMutableAttributedString(string: """
            Виділив текст — і він уже в буфері обміну.

            Ctrl+Option+V — вибрати одне з двох останніх скопійованих.
            Правий клік у текстовому полі — панель «1 / 2».
            Скопійоване зберігається лише в пам'яті, на диск і в мережу нічого не передається.


            """, attributes: base)
        var linkAttrs = base
        linkAttrs[.link] = URL(string: homePage)!
        credits.append(NSAttributedString(string: "Сторінка програми й оновлення", attributes: linkAttrs))
        NSApp.activate(ignoringOtherApps: true)
        NSApp.orderFrontStandardAboutPanel(options: [
            .applicationName: "AutoCopy",
            .applicationVersion: appVersion,
            .version: "",
            .credits: credits,
            NSApplication.AboutPanelOptionKey(rawValue: "Copyright"): "© 2026 ODIZH. Усі права захищені.",
        ])
    }

    func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
        } catch {
            let a = NSAlert()
            a.messageText = "Не вдалося змінити автозапуск"
            a.informativeText = "\(error.localizedDescription)\n\nМожна додати вручну: Системні параметри → Загальні → Елементи входу."
            NSApp.activate(ignoringOtherApps: true)
            a.runModal()
        }
    }

    func updateIcon() {
        let on = cfg.enabled && tap != nil
        statusItem.button?.image = AppDelegate.drawIcon(filled: on)
        statusItem.button?.toolTip = on ? "AutoCopy © 2026 ODIZH — увімкнено" : "AutoCopy © 2026 ODIZH — вимкнено"
    }

    // «A» in a rounded square; template image so it follows the light/dark menu bar
    static func drawIcon(filled: Bool) -> NSImage {
        let img = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { r in
            let box = NSBezierPath(roundedRect: r.insetBy(dx: 1, dy: 1), xRadius: 4, yRadius: 4)
            let font = NSFont.systemFont(ofSize: 12, weight: .bold)
            let a = NSAttributedString(string: "A", attributes: [.font: font, .foregroundColor: NSColor.black])
            let s = a.size()
            let p = NSPoint(x: r.midX - s.width / 2, y: r.midY - s.height / 2 + 0.5)
            if filled {
                NSColor.black.setFill(); box.fill()
                NSGraphicsContext.current?.compositingOperation = .destinationOut
                a.draw(at: p)
            } else {
                NSColor.black.setStroke(); box.lineWidth = 1.2; box.stroke()
                a.draw(at: p)
            }
            return true
        }
        img.isTemplate = true
        return img
    }

    func showExclusions() {
        if let w = exclWindow { NSApp.activate(ignoringOtherApps: true); w.makeKeyAndOrderFront(nil); return }
        let model = ExclModel(cfg.excluded, cfg.parked)
        let running = Array(Set(NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular }.map { appName($0) }.filter { !$0.isEmpty }))
            .sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
        let close: () -> Void = { [weak self] in self?.exclWindow?.close(); self?.exclWindow = nil }
        let view = ExclView(m: model, running: running, onSave: { [weak self] in
            self?.cfg.excluded = model.excluded
            self?.cfg.parked = model.parked
            self?.cfg.save()
            close()
        }, onCancel: close)
        let w = NSWindow(contentViewController: NSHostingController(rootView: view))
        w.title = "AutoCopy — де не копіювати"
        w.styleMask = [.titled, .closable, .resizable]
        w.isReleasedWhenClosed = false
        w.center()
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { [weak self] _ in
            self?.exclWindow = nil
        }
        exclWindow = w
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }
}

final class ClosureItem: NSMenuItem {
    private let run: () -> Void
    init(title: String, key: String = "", _ run: @escaping () -> Void) {
        self.run = run
        super.init(title: title, action: #selector(fire), keyEquivalent: key)
        target = self
        keyEquivalentModifierMask = []
    }
    required init(coder: NSCoder) { fatalError() }
    @objc func fire() { run() }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory) // menu bar only, no Dock icon
app.run()
