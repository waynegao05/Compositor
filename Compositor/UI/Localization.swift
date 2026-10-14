import Foundation

/// Localize display text only. Project values, command IDs and preference keys stay in English.
enum L10n {
    nonisolated static func tr(_ key: String) -> String {
        Bundle.main.localizedString(forKey: key, value: key, table: "Localizable")
    }

    nonisolated static func format(_ key: String, _ arguments: CVarArg...) -> String {
        String(format: tr(key), locale: Locale.current, arguments: arguments)
    }
}
