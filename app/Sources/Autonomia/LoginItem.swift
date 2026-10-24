import Foundation

/// Apertura al login tramite un LaunchAgent: con un'app compilata in locale e firmata ad-hoc
/// è più affidabile di SMAppService.
enum LoginItem {
    private static let label = Bundle.main.bundleIdentifier ?? "Autonomia"
    private static var plistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    static var isEnabled: Bool { FileManager.default.fileExists(atPath: plistURL.path) }

    static func setEnabled(_ on: Bool) {
        let fm = FileManager.default
        guard on else {
            try? fm.removeItem(at: plistURL)
            return
        }
        let bundle = Bundle.main.bundlePath
        let plist: [String: Any] = [
            "Label": label,
            "ProgramArguments": bundle.hasSuffix(".app") ? ["/usr/bin/open", "-a", bundle] : [bundle],
            "RunAtLoad": true,
            "KeepAlive": false
        ]
        try? fm.createDirectory(at: plistURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let data = try? PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0) {
            try? data.write(to: plistURL)
        }
    }
}
