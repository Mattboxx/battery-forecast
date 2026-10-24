import Foundation
import IOKit.ps

/// Stato della batteria letto da IOKit, la stessa fonte dell'icona di sistema.
struct PowerState: Equatable {
    var present = false
    var level = 0
    var onAC = false
    var charging = false
    var charged = false
    var minutesToEmpty: Int?     // stima di macOS: uso continuo al ritmo attuale
    var minutesToFull: Int?
}

enum Power {
    static func read() -> PowerState {
        var s = PowerState()
        guard let blob = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let list = IOPSCopyPowerSourcesList(blob)?.takeRetainedValue() as? [CFTypeRef] else { return s }
        for source in list {
            guard let d = IOPSGetPowerSourceDescription(blob, source)?.takeUnretainedValue() as? [String: Any],
                  (d[kIOPSTypeKey] as? String) == kIOPSInternalBatteryType else { continue }
            let current = d[kIOPSCurrentCapacityKey] as? Int ?? 0
            let max = d[kIOPSMaxCapacityKey] as? Int ?? 100
            s.present = true
            s.level = max > 0 ? Int((Double(current) / Double(max) * 100).rounded()) : current
            s.onAC = (d[kIOPSPowerSourceStateKey] as? String) == kIOPSACPowerValue
            s.charging = d[kIOPSIsChargingKey] as? Bool ?? false
            s.charged = d[kIOPSIsChargedKey] as? Bool ?? false
            if let t = d[kIOPSTimeToEmptyKey] as? Int, t > 0 { s.minutesToEmpty = t }
            if let t = d[kIOPSTimeToFullChargeKey] as? Int, t > 0 { s.minutesToFull = t }
        }
        return s
    }

    private static var handler: (() -> Void)?

    /// Chiama `onChange` a ogni cambio di livello o di alimentazione.
    static func observe(_ onChange: @escaping () -> Void) {
        handler = onChange
        let callback: IOPowerSourceCallbackType = { _ in Power.handler?() }
        if let source = IOPSNotificationCreateRunLoopSource(callback, nil)?.takeRetainedValue() {
            CFRunLoopAddSource(CFRunLoopGetMain(), source, .defaultMode)
        }
    }
}
