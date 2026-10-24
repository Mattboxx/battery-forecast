import Foundation

/// Testi dell'interfaccia. La chiave è il testo italiano; le traduzioni stanno in
/// app/Resources/{en,it}.lproj/Localizable.strings, che build.sh copia nell'app.
/// Senza traduzione resta la chiave, cioè l'italiano.
func L(_ key: String, _ args: CVarArg...) -> String {
    let format = Bundle.main.localizedString(forKey: key, value: key, table: nil)
    return args.isEmpty ? format : String(format: format, arguments: args)
}
