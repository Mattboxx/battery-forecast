import AppKit
import Charts
import SwiftUI

/// Il pannello che si apre dalla barra dei menu. Si usano gli stili di testo e i controlli di
/// sistema, così con l'SDK 26+ prende da solo l'aspetto di Tahoe (vetro, pulsanti a capsula).
struct PanelView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            if model.power.onAC { chargingCard } else { forecastCard }
            if let suggestion = model.savingSuggestion { SavingCard(model: model, suggestion: suggestion) }
            settingsCard
            VStack(alignment: .leading, spacing: 0) {
                MenuRow(title: L("Impostazioni Batteria…"), symbol: "gearshape", action: model.openBatterySettings)
                MenuRow(title: L("Esci da %@", Bundle.main.object(forInfoDictionaryKey: "CFBundleName") as? String ?? "Autonomia"), symbol: "power") { NSApp.terminate(nil) }
            }
        }
        .padding(16)
        .frame(width: 320)
    }

    // MARK: sezioni

    private var header: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .firstTextBaseline) {
                Text(L("Batteria")).font(.headline)
                Spacer()
                Text("\(model.power.level)%").font(.headline).monospacedDigit().foregroundStyle(.secondary)
            }
            Text(L("Fonte di alimentazione: %@", L(model.power.onAC ? "Alimentatore" : "Batteria")))
                .font(.subheadline)
                .foregroundStyle(.secondary)
            if model.lowPower {
                HStack(alignment: .firstTextBaseline) {
                    Label(L("Risparmio energetico attivo"), systemImage: "leaf.fill")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                    Spacer()
                    if model.savingBusy {
                        ProgressView().controlSize(.small)
                    } else {
                        Button(L("Disattiva…")) { model.setLowPower(false) }
                            .buttonStyle(.link)
                            .font(.subheadline)
                    }
                }
            }
        }
    }

    private var chargingCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 4) {
                SectionTitle(L("Autonomia prevista"))
                Text(chargingText).font(.body)
                Text(L("La previsione riparte quando stacchi l'alimentatore."))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var chargingText: String {
        if model.power.charged || model.power.level >= 100 { return L("Carica completa") }
        if model.power.charging, let m = model.power.minutesToFull {
            return L("In carica · completa tra %@", Format.longDuration(Double(m) * 60))
        }
        return L(model.power.charging ? "In carica" : "Alimentatore collegato")
    }

    private var forecastCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    SectionTitle(L("Autonomia prevista"))
                    Spacer()
                    if model.updating {
                        ProgressView().controlSize(.small)
                    } else if let f = model.activeForecast {
                        Text(L("alle %@", f.generated.formatted(.dateTime.hour().minute())))
                            .font(.footnote)
                            .foregroundStyle(.tertiary)
                    }
                }
                if let f = model.activeForecast {
                    forecastBody(f)
                        .opacity(model.isFresh ? 1 : 0.45)         // dati vecchi: si vedono, ma attenuati
                        .animation(.easeInOut(duration: 0.2), value: model.isFresh)
                } else if model.updating {
                    Text(L("Calcolo della previsione…")).font(.body).foregroundStyle(.secondary)
                } else {
                    Text(model.forecastError ?? L("Previsione non disponibile"))
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Button(L("Riprova")) { model.refresh(force: true) }.controlSize(.small)
                }
            }
        }
    }

    @ViewBuilder
    private func forecastBody(_ f: Forecast) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            if let empty = f.emptyDate {
                Text(L("Fino alle %@", Format.time(empty, relativeTo: model.now)))
                    .font(.title2.weight(.semibold))
                    .monospacedDigit()
                Text(L("circa %@ · tra %@ e %@", Format.longDuration(empty.timeIntervalSince(model.now)),
                       f.emptyEarly.map { Format.time($0) } ?? L("prima"),
                       f.emptyLate.map { Format.time($0) } ?? L("oltre %ld ore", f.horizon_hours)))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text(L("Oltre le prossime %ld ore", f.horizon_hours))
                    .font(.title2.weight(.semibold))
                Text(f.emptyEarly.map { L("Nella stima più prudente si scarica %@", Format.time($0)) }
                     ?? L("Anche nella stima più prudente resta sopra il %ld%%", Int(f.threshold)))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }

        if let p = model.endOfDayPoint {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(L("Alle %@", model.endOfDay.formatted(.dateTime.hour().minute()))).font(.body)
                Spacer()
                Text(L("tra %ld%% e %ld%%", Int(p.p10.rounded()), Int(p.p90.rounded())))
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                Text("\(Int(p.p50.rounded()))%")
                    .font(.body.weight(.semibold))
                    .monospacedDigit()
            }
        }

        ForecastChart(forecast: f, marker: model.endOfDay).frame(height: 84)

        VStack(alignment: .leading, spacing: 1) {
            Text(L("Impara dal tuo utilizzo · %@ giorni di storia", Format.days(f.history_days)))
            if let m = model.power.minutesToEmpty {
                Text(L("macOS: %@ di uso continuo al ritmo attuale", Format.duration(Double(m) * 60)))
            }
        }
        .font(.footnote)
        .foregroundStyle(.secondary)
        .fixedSize(horizontal: false, vertical: true)
    }

    private var settingsCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 10) {
                VStack(alignment: .leading, spacing: 6) {
                    Text(L("Nella barra dei menu")).font(.body)
                    Picker(L("Nella barra dei menu"), selection: $model.mode) {
                        ForEach(LabelMode.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }
                // Etichetta a sinistra e interruttore sul bordo destro, come nei pannelli di sistema.
                // Fuori da un Form, Toggle mette l'interruttore subito dopo il testo.
                HStack {
                    Text(L("Apri al login")).font(.body)
                    Spacer(minLength: 8)
                    Toggle(L("Apri al login"), isOn: Binding(get: { model.openAtLogin }, set: { model.setOpenAtLogin($0) }))
                        .labelsHidden()
                        .toggleStyle(.switch)
                        .controlSize(.small)
                }
            }
        }
    }
}

// MARK: proposta del risparmio energetico

struct SavingCard: View {
    @ObservedObject var model: AppModel
    let suggestion: SavingSuggestion

    private var title: String {
        switch suggestion {
        case .likely: return L("Potrebbe non bastare fino a sera")
        case .possible: return L("Margine ridotto per oggi")
        }
    }

    private var detail: String {
        switch suggestion {
        case .likely(let d):
            return L("Con il tuo uso abituale la batteria arriva fino alle %@.", Format.time(d, relativeTo: model.now))
        case .possible(let d):
            return L("Se lo usi più del solito, potresti restare senza batteria verso le %@.",
                     Format.time(d, relativeTo: model.now))
        }
    }

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "leaf.fill").foregroundStyle(.green).font(.body)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(title).font(.subheadline.weight(.semibold))
                        Text(detail)
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                HStack(spacing: 8) {
                    // I puntini dicono che segue una richiesta: macOS chiede la password.
                    Button(L("Attiva risparmio energetico…")) { model.setLowPower(true) }
                        .disabled(model.savingBusy)
                    if model.savingBusy { ProgressView().controlSize(.small) }
                }
                .controlSize(.small)
                Text(model.savingNote ?? L("macOS chiederà la password. Resta attivo quando usi la batteria, finché non lo disattivi."))
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if model.savingNote != nil {
                    Button(L("Apri Impostazioni Batteria…"), action: model.openBatterySettings)
                        .buttonStyle(.link)
                        .font(.footnote)
                }
            }
        }
    }
}

// MARK: componenti

struct ForecastChart: View {
    let forecast: Forecast
    let marker: Date

    /// La curva parte dal livello reale al momento della previsione.
    private var points: [ForecastPoint] {
        let start = ForecastPoint(t: forecast.generated_at, p10: forecast.level, p50: forecast.level, p90: forecast.level)
        return [start] + forecast.curve
    }

    var body: some View {
        Chart {
            ForEach(points) { p in
                AreaMark(x: .value("Ora", p.date),
                         yStart: .value("Prudente", p.p10),
                         yEnd: .value("Ottimista", p.p90))
                    .foregroundStyle(Color.accentColor.opacity(0.18))
                    .interpolationMethod(.monotone)
            }
            ForEach(points) { p in
                LineMark(x: .value("Ora", p.date), y: .value("Previsto", p.p50))
                    .foregroundStyle(Color.accentColor)
                    .lineStyle(StrokeStyle(lineWidth: 2, lineCap: .round, lineJoin: .round))
                    .interpolationMethod(.monotone)
            }
            if marker < forecast.horizonEnd, let p = forecast.point(at: marker) {
                RuleMark(x: .value("Fine giornata", marker))
                    .foregroundStyle(.secondary.opacity(0.4))
                    .lineStyle(StrokeStyle(lineWidth: 1, dash: [2, 3]))
                PointMark(x: .value("Fine giornata", marker), y: .value("Previsto", p.p50))
                    .foregroundStyle(Color.accentColor)
                    .symbolSize(36)
            }
        }
        .chartYScale(domain: 0...100)
        .chartXScale(domain: forecast.generated...forecast.horizonEnd)
        .chartYAxis {
            AxisMarks(position: .trailing, values: [0, 50, 100]) { v in
                AxisGridLine(stroke: StrokeStyle(lineWidth: 0.5)).foregroundStyle(.quaternary)
                AxisValueLabel {
                    Text("\(v.as(Int.self) ?? 0)%").font(.caption2).foregroundStyle(.secondary)
                }
            }
        }
        .chartXAxis {
            AxisMarks(values: clockTicks) { v in
                AxisValueLabel(centered: false) {
                    if let d = v.as(Date.self) {
                        Text(d, format: .dateTime.hour().minute()).font(.caption2).foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    /// Le 00:00, 06:00, 12:00 e 18:00 comprese nell'intervallo: ore tonde, non l'ora del calcolo.
    private var clockTicks: [Date] {
        let cal = Calendar.current
        guard var d = cal.nextDate(after: forecast.generated, matching: DateComponents(minute: 0, second: 0),
                                   matchingPolicy: .nextTime) else { return [] }
        var ticks: [Date] = []
        while d < forecast.horizonEnd {
            if cal.component(.hour, from: d) % 6 == 0 { ticks.append(d) }
            d = d.addingTimeInterval(3600)
        }
        return ticks
    }
}

/// Riquadro con angoli continui, come i moduli di Centro di Controllo.
struct Card<Content: View>: View {
    let content: Content
    init(@ViewBuilder content: () -> Content) { self.content = content() }
    var body: some View {
        content
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
    }
}

struct SectionTitle: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text).font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
    }
}

/// Riga cliccabile con l'evidenziazione al passaggio, come le voci dei menu di sistema.
struct MenuRow: View {
    let title: String
    let symbol: String
    let action: () -> Void

    // SDK 27 ha trasformato @State in una macro che con i soli Command Line Tools non si
    // espande: si usa direttamente lo struct property wrapper.
    private var _hover = State(wrappedValue: false)
    private var hover: Bool {
        get { _hover.wrappedValue }
        nonmutating set { _hover.wrappedValue = newValue }
    }

    init(title: String, symbol: String, action: @escaping () -> Void) {
        self.title = title
        self.symbol = symbol
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                Image(systemName: symbol).frame(width: 16).foregroundStyle(.secondary)
                Text(title).font(.body)
                Spacer(minLength: 0)
            }
            // Stessa griglia dei riquadri: l'evidenziazione è larga quanto un riquadro e l'icona
            // sta sulla stessa verticale del testo al loro interno (12 pt dal bordo).
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(hover ? Color.primary.opacity(0.1) : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}
