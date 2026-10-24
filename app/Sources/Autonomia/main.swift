import AppKit
import SwiftUI

// Scrivere su un servizio già chiuso non deve far morire l'app: l'errore si gestisce in ForecastService.
signal(SIGPIPE, SIG_IGN)

// `Autonomia --snapshot <cartella>` salva il pannello (chiaro e scuro) e la voce della barra
// come PNG con i dati veri, senza aprire l'app. I controlli AppKit (selettore, interruttore) e il
// vetro non vengono disegnati da questo renderer: per l'aspetto reale serve il pannello aperto.
let arguments = CommandLine.arguments
if let i = arguments.firstIndex(of: "--snapshot"), i + 1 < arguments.count {
    let dir = URL(fileURLWithPath: arguments[i + 1])
    MainActor.assumeIsolated { Snapshot.render(into: dir) }   // il codice di primo livello gira sul main thread
    exit(0)
}
AutonomiaApp.main()

@MainActor
enum Snapshot {
    static func render(into dir: URL) {
        _ = NSApplication.shared
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let model = AppModel(live: false)
        if model.power.present && !model.power.onAC {
            var done = false
            ForecastService.shared.forecast(level: model.power.level, onAC: false) { result in
                if case .success(let f) = result { model.forecast = f }
                done = true
            }
            let deadline = Date().addingTimeInterval(30)
            while !done && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) }
            ForecastService.shared.stop()
        }
        for (name, appearance) in [("panel-light", NSAppearance.Name.aqua), ("panel-dark", .darkAqua)] {
            saveHosted(PanelView(model: model).background(Color(nsColor: .windowBackgroundColor)),
                       appearance: appearance, to: dir.appendingPathComponent("\(name).png"))
        }
        // Il riquadro della proposta e l'intestazione con il risparmio attivo, che con una batteria
        // abbondante non compaiono: si mostrano forzando lo stato (il modello delle istantanee è finto).
        let proposal = AppModel(live: false)
        proposal.lowPower = false
        let failed = AppModel(live: false)
        failed.savingNote = L("Non è stato possibile cambiare il risparmio energetico.")
        let eve = Date().addingTimeInterval(3 * 3600 + 20 * 60)
        for (name, appearance) in [("saving-light", NSAppearance.Name.aqua), ("saving-dark", .darkAqua)] {
            saveHosted(VStack(spacing: 12) {
                SavingCard(model: proposal, suggestion: .likely(eve))
                SavingCard(model: failed, suggestion: .possible(eve))
            }.padding(16).frame(width: 320).background(Color(nsColor: .windowBackgroundColor)),
                       appearance: appearance, to: dir.appendingPathComponent("\(name).png"))
        }
        let active = AppModel(live: false)
        active.lowPower = true
        active.forecast = model.forecast
        saveHosted(PanelView(model: active).background(Color(nsColor: .windowBackgroundColor)),
                   appearance: .darkAqua, to: dir.appendingPathComponent("panel-lowpower-dark.png"))
        for mode in LabelMode.allCases {
            model.mode = mode
            save(StatusLabel(model: model).padding(.horizontal, 8).padding(.vertical, 4)
                    .background(Color(white: 0.92)).environment(\.colorScheme, .light),
                 to: dir.appendingPathComponent("label-\(mode.rawValue).png"))
        }
        print("istantanee in \(dir.path)")
    }

    /// Disegna la vista dentro una finestra vera (fuori schermo): i controlli di sistema escono con
    /// lo stile del SDK con cui l'app è collegata, cosa che ImageRenderer non fa.
    private static func saveHosted<V: View>(_ view: V, appearance: NSAppearance.Name, to url: URL) {
        let host = NSHostingView(rootView: view)
        let size = host.fittingSize
        host.frame = NSRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: NSRect(x: -20000, y: -20000, width: size.width, height: size.height),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: appearance)
        window.contentView = host
        window.orderFrontRegardless()
        host.layoutSubtreeIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.6))   // il tempo di far assestare SwiftUI
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return }
        host.cacheDisplay(in: host.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
        window.orderOut(nil)
    }

    private static func save<V: View>(_ view: V, to url: URL) {
        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        guard let cg = renderer.cgImage,
              let png = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:]) else {
            print("render fallito: \(url.lastPathComponent)")
            return
        }
        try? png.write(to: url)
    }
}
