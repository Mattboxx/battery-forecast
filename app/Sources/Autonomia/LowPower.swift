import Foundation
import OSLog

/// Cambiare la modalità risparmio energetico richiede i permessi di root (`pmset`) e Apple non
/// offre un'API. Si usa la finestra di autorizzazione di sistema (AppleScript con privilegi di
/// amministratore): macOS la mostra a nome di Autonomia e l'app non vede mai la password.
enum LowPower {
    private static let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "Autonomia", category: "lowpower")

    enum Outcome: Equatable {
        case done
        case cancelled                 // l'utente ha chiuso la finestra della password
        case failed(String)
    }

    /// `-b` vale per quando si usa la batteria: è «Solo con la batteria» di Impostazioni Batteria.
    /// Con l'alimentatore collegato il risparmio energetico si spegne da solo e al distacco riparte.
    static func set(_ on: Bool, completion: @escaping (Outcome) -> Void) {
        run(source: "do shell script \"/usr/bin/pmset -b lowpowermode \(on ? 1 : 0)\" with administrator privileges",
            completion: completion)
    }

    /// NSAppleScript non regge l'uso da più thread insieme (provato: gli esiti si confondono):
    /// gli script passano uno alla volta da questa coda.
    private static let queue = DispatchQueue(label: "applescript", qos: .userInitiated)

    /// Esegue lo script fuori dal thread principale: la finestra della password resta aperta finché
    /// l'utente risponde e il pannello non deve bloccarsi nell'attesa.
    static func run(source: String, completion: @escaping (Outcome) -> Void) {
        queue.async {
            var error: NSDictionary?
            _ = NSAppleScript(source: source)?.executeAndReturnError(&error)
            let outcome: Outcome
            if let error {
                let code = error[NSAppleScript.errorNumber] as? Int
                let message = error[NSAppleScript.errorMessage] as? String ?? L("errore sconosciuto")
                outcome = code == -128 ? .cancelled : .failed(message)   // -128: «Annulla» nella finestra
                if code != -128 { log.notice("risparmio energetico non cambiato (\(code ?? 0)): \(message, privacy: .public)") }
            } else {
                outcome = .done
            }
            DispatchQueue.main.async { completion(outcome) }
        }
    }
}
