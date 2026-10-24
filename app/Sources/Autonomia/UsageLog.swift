import Foundation

/// Registro dell'utilizzo: livello e alimentazione ogni ~5 minuti, finché l'app è aperta.
/// macOS tiene i propri dati solo per circa una settimana: questo file è la memoria lunga
/// che la previsione legge a ogni giro (fino a ~110 giorni, il contesto massimo di Timestral).
enum UsageLog {
    static let url: URL = {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Autonomia", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("battery_log.csv")
    }()

    private static var last: (date: Date, level: Int, onAC: Bool)?

    static func record(_ s: PowerState) {
        guard s.present else { return }
        let now = Date()
        if let l = last, now.timeIntervalSince(l.date) < 270, l.level == s.level, l.onAC == s.onAC { return }
        let line = "\(Int(now.timeIntervalSince1970)),\(s.level),\(s.onAC ? 1 : 0)\n"
        if !FileManager.default.fileExists(atPath: url.path) {
            try? Data("ts,level,ac\n".utf8).write(to: url)
        }
        if let h = try? FileHandle(forWritingTo: url) {
            h.seekToEndOfFile()
            h.write(Data(line.utf8))
            try? h.close()
        }
        last = (now, s.level, s.onAC)
    }
}
