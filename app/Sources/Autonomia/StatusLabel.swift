import AppKit
import SwiftUI

/// Voce nella barra dei menu: testo a sinistra e batteria a destra, come quella di sistema.
struct StatusLabel: View {
    @ObservedObject var model: AppModel

    var body: some View {
        let text = model.labelText
        HStack(spacing: 4) {
            if !text.isEmpty { Text(text).monospacedDigit() }
            Image(nsImage: BatteryGlyph.image(level: model.power.level, charging: model.power.onAC))
        }
    }
}

/// Batteria disegnata con il riempimento esatto, come immagine template: macOS la ricolora
/// da solo per la barra chiara, scura e per lo stato selezionato.
enum BatteryGlyph {
    static func image(level: Int, charging: Bool) -> NSImage {
        if charging, let symbol = NSImage(systemSymbolName: "battery.100percent.bolt",
                                          accessibilityDescription: L("In carica")) {
            let img = symbol.withSymbolConfiguration(.init(pointSize: 13, weight: .regular)) ?? symbol
            img.isTemplate = true
            return img
        }
        let img = NSImage(size: NSSize(width: 27, height: 13), flipped: false) { _ in
            let body = NSRect(x: 0.5, y: 0.5, width: 23, height: 12)
            let outline = NSBezierPath(roundedRect: body, xRadius: 3.5, yRadius: 3.5)
            outline.lineWidth = 1
            NSColor.black.withAlphaComponent(0.45).setStroke()
            outline.stroke()
            NSColor.black.withAlphaComponent(0.45).setFill()
            NSBezierPath(roundedRect: NSRect(x: 24.5, y: 4.25, width: 1.75, height: 4.5),
                         xRadius: 0.9, yRadius: 0.9).fill()
            let inner = body.insetBy(dx: 2, dy: 2)
            let width = max(1.5, inner.width * CGFloat(min(max(level, 0), 100)) / 100)
            NSColor.black.setFill()
            NSBezierPath(roundedRect: NSRect(x: inner.minX, y: inner.minY, width: width, height: inner.height),
                         xRadius: 1.5, yRadius: 1.5).fill()
            return true
        }
        img.isTemplate = true
        img.accessibilityDescription = L("Batteria %ld%%", level)
        return img
    }
}
