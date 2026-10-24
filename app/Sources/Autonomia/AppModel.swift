import AppKit
import SwiftUI

enum LabelMode: String, CaseIterable, Identifiable {
    case durata, fineGiornata, percentuale
    var id: String { rawValue }
    var title: String {
        switch self {
        case .durata: return L("Durata")
        case .fineGiornata: return L("Fine giornata")
        case .percentuale: return "%"
        }
    }
}

/// Quando proporre il risparmio energetico, in base alla previsione con l'uso abituale.
enum SavingSuggestion: Equatable {
    case likely(Date)      // la stima centrale arriva a zero prima di sera
    case possible(Date)    // solo la stima prudente arriva a zero prima di sera
}

final class AppModel: ObservableObject {
    @Published var power = PowerState()
    @Published var forecast: Forecast?
    @Published var forecastError: String?
    @Published var updating = false
    @Published var now = Date()
    @Published var mode: LabelMode {
        didSet { if persists { UserDefaults.standard.set(mode.rawValue, forKey: "labelMode") } }
    }
    @Published var openAtLogin = LoginItem.isEnabled
    @Published var lowPower = ProcessInfo.processInfo.isLowPowerModeEnabled
    @Published var savingBusy = false
    @Published var savingNote: String?

    private var lastRun: Date?
    private var timer: Timer?
    private let persists: Bool        // le istantanee non devono toccare le preferenze vere

    /// `live: false` serve alle istantanee: niente timer, osservatori né registro.
    init(live: Bool = true) {
        persists = live
        mode = LabelMode(rawValue: UserDefaults.standard.string(forKey: "labelMode") ?? "") ?? .durata
        power = Power.read()
        forecast = ForecastCache.load()           // si mostra subito, in attesa di una più fresca
        lastRun = forecast?.generated
        guard live else { return }
        if openAtLogin { LoginItem.setEnabled(true) }   // riallinea il percorso se l'app è stata spostata
        UsageLog.record(power)
        Power.observe { [weak self] in self?.powerChanged() }
        let timer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in self?.tick() }
        timer.tolerance = 20                      // permette a macOS di accorpare i risvegli
        self.timer = timer
        let center = NotificationCenter.default
        center.addObserver(forName: .NSProcessInfoPowerStateDidChange, object: nil, queue: .main) { [weak self] _ in
            self?.lowPower = ProcessInfo.processInfo.isLowPowerModeEnabled
        }
        center.addObserver(forName: NSWindow.didBecomeKeyNotification, object: nil, queue: .main) { [weak self] _ in
            self?.panelDidOpen()                  // l'unica finestra dell'app è il pannello
        }
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            self?.tick()
            self?.refresh(force: true)
        }
        if power.present && !power.onAC { ForecastService.shared.warmUp() }   // pronto prima che serva
        refresh(force: false)
    }

    // MARK: aggiornamenti

    private func powerChanged() {
        let wasOnAC = power.onAC
        power = Power.read()
        UsageLog.record(power)
        if wasOnAC && !power.onAC { refresh(force: true) }   // appena staccato: previsione nuova
    }

    private func tick() {
        now = Date()
        power = Power.read()
        UsageLog.record(power)
        refresh(force: false)
    }

    /// All'apertura del pannello i dati devono essere freschi: se non lo sono, parte il ricalcolo
    /// (~70 ms a servizio acceso) e intanto si vede l'ultima previsione, attenuata.
    private func panelDidOpen() {
        now = Date()
        power = Power.read()
        lowPower = ProcessInfo.processInfo.isLowPowerModeEnabled
        if !power.onAC && !isFresh { refresh(force: true) }
    }

    /// Ogni 10 minuti a batteria, o prima se il livello è sceso di 3 punti rispetto alla previsione.
    func refresh(force: Bool) {
        guard power.present, !power.onAC, !updating else { return }
        if !force {
            let age = lastRun.map { Date().timeIntervalSince($0) } ?? .infinity
            let drift = forecast.map { abs(Double(power.level) - $0.level) } ?? 99
            guard age >= 600 || (drift >= 3 && age >= 120) else { return }
        }
        updating = true
        lastRun = Date()
        ForecastService.shared.forecast(level: power.level, onAC: power.onAC) { [weak self] result in
            guard let self else { return }
            self.updating = false
            self.now = Date()
            switch result {
            case .success(let f):
                self.forecast = f
                self.forecastError = nil
            case .failure(let e):
                self.forecastError = e.localizedDescription
            }
        }
    }

    func setOpenAtLogin(_ on: Bool) {
        LoginItem.setEnabled(on)
        openAtLogin = LoginItem.isEnabled
    }

    // MARK: risparmio energetico

    /// Chiede la password di amministratore a macOS e cambia il risparmio energetico (vedi LowPower).
    /// Se la richiesta fallisce resta la nota con l'errore e il collegamento a Impostazioni Batteria.
    func setLowPower(_ on: Bool) {
        savingNote = nil
        savingBusy = true
        LowPower.set(on) { [weak self] outcome in
            guard let self else { return }
            self.savingBusy = false
            switch outcome {
            case .done, .cancelled: break
            case .failed: self.savingNote = L("Non è stato possibile cambiare il risparmio energetico.")
            }
            // Il sistema notifica il cambio, ma si rilegge comunque: è lo stato vero a contare.
            self.lowPower = ProcessInfo.processInfo.isLowPowerModeEnabled
        }
    }

    func openBatterySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.Battery-Settings.extension") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: valori mostrati

    /// La previsione vale solo a batteria e se è recente (dopo una ricarica non vale più).
    var activeForecast: Forecast? {
        guard let f = forecast, !power.onAC, now.timeIntervalSince(f.generated) < 3 * 3600 else { return nil }
        return f
    }

    /// Fresca = di meno di 5 minuti e con il livello di adesso.
    var isFresh: Bool {
        guard let f = activeForecast else { return false }
        return now.timeIntervalSince(f.generated) < 300 && abs(Double(power.level) - f.level) <= 1
    }

    /// "Fine giornata": le 22, oppure fra due ore se è già sera.
    var endOfDay: Date {
        let cal = Calendar.current
        let ten = cal.date(bySettingHour: 22, minute: 0, second: 0, of: now) ?? now
        return now < ten.addingTimeInterval(-2 * 3600) ? ten : now.addingTimeInterval(2 * 3600)
    }

    var endOfDayPoint: ForecastPoint? { activeForecast?.point(at: endOfDay) }

    /// La previsione contiene già l'uso abituale: se anche così finisce prima di sera, si propone.
    var savingSuggestion: SavingSuggestion? {
        guard !lowPower, let f = activeForecast else { return nil }
        if let e = f.emptyDate, e > now, e < endOfDay { return .likely(e) }
        if let e = f.emptyEarly, e > now, e < endOfDay { return .possible(e) }
        return nil
    }

    var labelText: String {
        guard power.present else { return "" }
        guard !power.onAC, let f = activeForecast else { return "\(power.level)%" }
        switch mode {
        case .percentuale:
            return "\(power.level)%"
        case .fineGiornata:
            return endOfDayPoint.map { "→ \(Int($0.p50.rounded()))%" } ?? "\(power.level)%"
        case .durata:
            guard let empty = f.emptyDate, empty > now else { return "\(f.horizon_hours)h+" }
            return Format.duration(empty.timeIntervalSince(now))
        }
    }
}

enum Format {
    /// 5:10 — ore e minuti, come la vecchia stima nella barra dei menu.
    static func duration(_ seconds: TimeInterval) -> String {
        let m = max(0, Int(seconds / 60))
        return String(format: "%d:%02d", m / 60, m % 60)
    }

    static func longDuration(_ seconds: TimeInterval) -> String {
        let m = max(0, Int(seconds / 60))
        if m < 60 { return "\(m) min" }
        return m % 60 == 0 ? "\(m / 60) h" : "\(m / 60) h \(m % 60) min"
    }

    static func time(_ d: Date, relativeTo now: Date = Date()) -> String {
        let cal = Calendar.current
        let hm = d.formatted(.dateTime.hour().minute())
        if cal.isDate(d, inSameDayAs: now) { return hm }
        if let tomorrow = cal.date(byAdding: .day, value: 1, to: now), cal.isDate(d, inSameDayAs: tomorrow) {
            return L("domani alle %@", hm)
        }
        return d.formatted(.dateTime.weekday(.wide).hour().minute())
    }

    static func days(_ d: Double) -> String {
        d.formatted(.number.precision(.fractionLength(d < 10 ? 1 : 0)))
    }
}
