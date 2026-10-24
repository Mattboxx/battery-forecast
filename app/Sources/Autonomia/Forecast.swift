import Foundation
import os

struct ForecastPoint: Decodable, Identifiable {
    let t: Double
    let p10: Double
    let p50: Double
    let p90: Double
    var id: Double { t }
    var date: Date { Date(timeIntervalSince1970: t) }
}

/// Uscita di scripts/battery_now.py nel progetto Timestral.
struct Forecast: Decodable {
    let generated_at: Double
    let level: Double
    let threshold: Double
    let horizon_hours: Int
    let history_days: Double
    let own_log_days: Double
    let empty_at: Double?
    let empty_early: Double?
    let empty_late: Double?
    let curve: [ForecastPoint]
    let elapsed_ms: Int

    var generated: Date { Date(timeIntervalSince1970: generated_at) }
    var emptyDate: Date? { empty_at.map(Date.init(timeIntervalSince1970:)) }
    var emptyEarly: Date? { empty_early.map(Date.init(timeIntervalSince1970:)) }
    var emptyLate: Date? { empty_late.map(Date.init(timeIntervalSince1970:)) }
    var horizonEnd: Date { generated.addingTimeInterval(Double(horizon_hours) * 3600) }

    /// Il punto della curva più vicino a `date`.
    func point(at date: Date) -> ForecastPoint? {
        curve.min { abs($0.date.timeIntervalSince(date)) < abs($1.date.timeIntervalSince(date)) }
    }
}

enum ForecastError: LocalizedError {
    case missingProject(String), failed(String)
    var errorDescription: String? {
        switch self {
        case .missingProject(let p): return L("Ambiente di previsione non trovato in %@. Esegui app/build.sh.", p)
        case .failed(let m): return m
        }
    }
}

/// Ultima previsione su disco: all'apertura del pannello si mostra subito, senza aspettare il calcolo.
enum ForecastCache {
    private static let url: URL = {
        let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Autonomia", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("last_forecast.json")
    }()

    static func save(_ data: Data) { try? data.write(to: url, options: .atomic) }

    static func load() -> Forecast? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(Forecast.self, from: data)
    }
}

/// Il servizio Python di Timestral: si avvia una volta (~2,7 s, carica PyTorch e il modello) e poi
/// risponde a ogni richiesta in ~70 ms e ~0,3 J, contro i ~2,8 s e ~14 J di un avvio a ogni previsione.
/// Tiene ~310 MB di memoria finché è vivo: si ferma da solo dopo 15 minuti senza richieste
/// (cioè quando il Mac è in carica e non servono previsioni). Tutto avviene sul thread principale.
final class ForecastService {
    static let shared = ForecastService()

    private var process: Process?
    private var input: FileHandle?
    private var buffer = Data()
    private var errorTail = ""
    private var nextID = 1
    private var waiting: [Int: (Result<Forecast, Error>) -> Void] = [:]
    private var backlog: [Data] = []
    private var isReady = false
    private var startedAt = Date()
    private var sentAt: [Int: Date] = [:]
    private var idleTimer: Timer?
    private let idleLimit: TimeInterval = 15 * 60

    /// Ambiente Python e script, preparati da build.sh in Application Support. NON stanno nel progetto
    /// in ~/Documents: lì macOS chiede all'app il permesso per ogni file letto dal servizio, il servizio
    /// resta fermo finché nessuno risponde, e la richiesta ritorna a ogni ricompilazione (firma ad-hoc).
    /// Nell'app scaricata (release.sh) Python, dipendenze, script e modello stanno invece nel bundle,
    /// in Contents/Resources/runtime: si usa quello se c'è.
    static let runtime: URL = {
        if let r = Bundle.main.resourceURL?.appendingPathComponent("runtime", isDirectory: true),
           FileManager.default.isExecutableFile(atPath: r.appendingPathComponent("python/bin/python3").path) {
            return r
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Autonomia/runtime", isDirectory: true)
    }()

    /// Con `.utility` su batteria il servizio gira sui core efficienti e l'avvio di PyTorch (28 G di
    /// istruzioni) passa da ~3 s a ~60 s: la previsione sembra non arrivare mai. Misurato il 22/10/2026.
    static let qos: QualityOfService = .userInitiated
    static var qosName: String { qos == .utility ? "utility" : "userInitiated" }
    private static let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "Autonomia", category: "forecast")

    var isWarm: Bool { isReady && process?.isRunning == true }

    func forecast(level: Int, onAC: Bool, completion: @escaping (Result<Forecast, Error>) -> Void) {
        do { try startIfNeeded() } catch {
            completion(.failure(error))
            return
        }
        let id = nextID
        nextID += 1
        waiting[id] = completion
        sentAt[id] = Date()
        let data = Data("{\"id\":\(id),\"level\":\(level),\"ac\":\(onAC ? 1 : 0)}\n".utf8)
        if isReady { send(data) } else { backlog.append(data) }
        armIdleTimer()
    }

    /// Avvia il servizio senza fare richieste, così è pronto quando serve.
    func warmUp() {
        try? startIfNeeded()
        armIdleTimer()
    }

    func stop() {
        idleTimer?.invalidate()
        guard let p = process else { return }
        try? input?.close()                           // stdin chiuso: il servizio esce da solo
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { if p.isRunning { p.terminate() } }
    }

    // MARK: processo

    private func startIfNeeded() throws {
        if let p = process, p.isRunning { return }
        guard let python = ["python/bin/python3", "venv/bin/python"].map(Self.runtime.appendingPathComponent)
                .first(where: { FileManager.default.isExecutableFile(atPath: $0.path) }) else {
            throw ForecastError.missingProject(Self.runtime.path)
        }
        let p = Process()
        p.executableURL = python
        // Il bundle è firmato: Python non deve scriverci i .pyc né leggere pacchetti dell'utente.
        var env = ProcessInfo.processInfo.environment
        env["PYTHONDONTWRITEBYTECODE"] = "1"
        env["PYTHONNOUSERSITE"] = "1"
        let model = Self.runtime.appendingPathComponent("model")
        if FileManager.default.fileExists(atPath: model.path) { env["AUTONOMIA_MODEL"] = model.path }
        p.environment = env
        p.arguments = [Self.runtime.appendingPathComponent("battery_now.py").path, "--serve"]
        p.currentDirectoryURL = Self.runtime
        p.qualityOfService = Self.qos
        let out = Pipe(), err = Pipe(), inp = Pipe()
        // I lati che usa solo l'app non devono finire nel processo figlio: se il figlio tenesse aperto
        // anche il lato di scrittura del proprio stdin, non vedrebbe mai la fine del flusso.
        for fd in [out.fileHandleForReading.fileDescriptor, err.fileHandleForReading.fileDescriptor,
                   inp.fileHandleForWriting.fileDescriptor] {
            _ = fcntl(fd, F_SETFD, FD_CLOEXEC)
        }
        p.standardOutput = out
        p.standardError = err
        p.standardInput = inp
        out.fileHandleForReading.readabilityHandler = { [weak self] h in
            let d = h.availableData
            if d.isEmpty { h.readabilityHandler = nil }   // EOF: senza questo il gestore girerebbe a vuoto
            DispatchQueue.main.async { self?.received(d, from: p) }
        }
        err.fileHandleForReading.readabilityHandler = { [weak self] h in
            let d = h.availableData
            if d.isEmpty { h.readabilityHandler = nil; return }
            if let s = String(data: d, encoding: .utf8),
               let last = s.split(separator: "\n").last(where: { !$0.contains("Warning") }) {
                DispatchQueue.main.async { self?.errorTail = String(last) }
            }
        }
        p.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async { self?.terminated(proc) }
        }
        try p.run()
        process = p
        input = inp.fileHandleForWriting
        buffer = Data()
        isReady = false
        startedAt = Date()
    }

    private func send(_ data: Data) {
        do { try input?.write(contentsOf: data) } catch {
            failAll(ForecastError.failed(L("Il servizio di previsione non risponde")))
        }
    }

    private func received(_ data: Data, from p: Process) {
        guard p === process, !data.isEmpty else { return }
        buffer.append(data)
        while let nl = buffer.firstIndex(of: 10) {
            let line = buffer[buffer.startIndex..<nl]
            buffer.removeSubrange(buffer.startIndex...nl)
            handle(Data(line))
        }
    }

    private func handle(_ line: Data) {
        guard let obj = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any] else { return }
        if obj["ready"] != nil {
            isReady = true
            Self.log.notice("servizio pronto dopo \(Date().timeIntervalSince(self.startedAt), format: .fixed(precision: 1), privacy: .public) s, QoS \(Self.qosName, privacy: .public)")
            let queued = backlog
            backlog = []
            queued.forEach(send)
            return
        }
        guard let id = obj["id"] as? Int, let done = waiting.removeValue(forKey: id) else { return }
        if let t = sentAt.removeValue(forKey: id) {
            Self.log.notice("previsione in \(Date().timeIntervalSince(t) * 1000, format: .fixed(precision: 0), privacy: .public) ms, calcolo interno \(String(describing: obj["elapsed_ms"] ?? "?"), privacy: .public) ms")
        }
        if let message = obj["error"] as? String {
            done(.failure(ForecastError.failed(message)))
        } else {
            done(Result { try JSONDecoder().decode(Forecast.self, from: line) }.map { f in
                ForecastCache.save(line)
                return f
            })
        }
    }

    private func terminated(_ p: Process) {
        guard p === process else { return }
        process = nil
        input = nil
        isReady = false
        failAll(ForecastError.failed(errorTail.isEmpty ? L("Il servizio di previsione si è fermato") : errorTail))
    }

    private func failAll(_ error: Error) {
        let callbacks = waiting.values
        waiting = [:]
        backlog = []
        callbacks.forEach { $0(.failure(error)) }
    }

    private func armIdleTimer() {
        idleTimer?.invalidate()
        idleTimer = Timer.scheduledTimer(withTimeInterval: idleLimit, repeats: false) { [weak self] _ in
            self?.stop()
        }
    }
}
